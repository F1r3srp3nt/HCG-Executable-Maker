using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Account.Support;
using HCG.ExecutableMaker.App.Services;

namespace HCG.ExecutableMaker.App.ViewModels;

public sealed record RequestRow(SupportRequestSummary Summary)
{
    public string Title => $"{Summary.Reference} · {Summary.Title}";
    public string Detail => $"{SupportStatusText.Label(Summary.Status)} · updated {Summary.UpdatedAt.LocalDateTime:g}" +
                            (Summary.StaffMessages > 0 ? $" · {Summary.StaffMessages} message(s) from HCG" : "");
    public StatusTag Status => new(SupportStatusText.Label(Summary.Status), Summary.Status switch
    {
        SupportStatus.MoreInformationNeeded => "warning",
        SupportStatus.Completed => "success",
        SupportStatus.Draft or SupportStatus.Closed => "neutral",
        _ => "info",
    });
}

public sealed record MessageRow(SupportMessage Message)
{
    public string Author => Message.FromStaff ? (Message.AuthorName ?? "HCG Support") : "You";
    public string When => Message.CreatedAt.LocalDateTime.ToString("g");
    public string Body => Message.Body;
    public bool FromStaff => Message.FromStaff;
    public string Kind => Message.Kind switch { "more_information" => "More information needed", "status" => "Status", "response" => "Response", _ => "" };
}

public sealed record AttachmentRow(SupportAttachmentInfo Attachment)
{
    public string Name => Attachment.Name;
    public string Detail => $"{Format.Bytes(Attachment.SizeBytes)} · {Attachment.State}{(Attachment.Reason is { } r ? " — " + r : "")}" +
                            (Attachment.DeleteAfter is { } d && Attachment.State != "deleted" ? $" · deleted after {d.LocalDateTime:d}" : "");
    public bool CanDownload => Attachment.State == "received";
}

/// <summary>My requests: status, timestamps, HCG staff responses, replies, attachments, withdrawal.</summary>
public sealed partial class MyRequestsViewModel(HcgAccountService account, AppAccessService access, SupportService support,
    IPlatformShell shell, IDialogService dialogs, NavigationService navigation) : PageViewModel
{
    public override string Title => "My requests";
    public override string Subtitle => "Your support requests to HCG and HCG's responses.";

    public ObservableCollection<RequestRow> Requests { get; } = [];
    public ObservableCollection<MessageRow> Messages { get; } = [];
    public ObservableCollection<AttachmentRow> Attachments { get; } = [];
    public ObservableCollection<DiagnosticEntry> SentDiagnostics { get; } = [];
    [ObservableProperty] private RequestRow? _selected;
    [ObservableProperty] private SupportRequestDetail? _detail;
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private bool _canLoad;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SendReplyCommand))] private string _reply = "";
    [ObservableProperty] private string _detailSummary = "";
    [ObservableProperty] private bool _canReply;
    [ObservableProperty] private bool _canWithdraw;

    public override Task OnNavigatedToAsync(object? parameter) => LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        ErrorMessage = null;
        if (!account.IsSignedIn)
        {
            CanLoad = false;
            State = account.Status == HcgAccountStatus.NotConfigured
                ? "HCG sign-in is not available in this version yet, so there are no requests to show."
                : "Sign in with HCG (Account page) to see your support requests.";
            Requests.Clear();
            IsEmpty = false;
            return;
        }
        IsBusy = true;
        try
        {
            var list = await support.ListAsync();
            CanLoad = true;
            var keep = Selected?.Summary.Id;
            Requests.Clear();
            foreach (var r in list.Requests) Requests.Add(new RequestRow(r));
            IsEmpty = Requests.Count == 0;
            State = IsEmpty ? "You have no support requests yet. Use Help & Support, or “Request project support” on an unsupported project." : "";
            Selected = Requests.FirstOrDefault(r => r.Summary.Id == keep) ?? Requests.FirstOrDefault();
        }
        catch (HcgApiException ex)
        {
            CanLoad = false;
            State = ex.Message;
            if (ex.Failure is HcgApiFailure.Forbidden) State += " " + access.Message;
        }
        finally { IsBusy = false; }
    }

    partial void OnSelectedChanged(RequestRow? value) => _ = LoadDetailAsync(value);

    private async Task LoadDetailAsync(RequestRow? row)
    {
        Messages.Clear();
        Attachments.Clear();
        SentDiagnostics.Clear();
        Detail = null;
        if (row is null) return;
        try
        {
            var d = await support.GetAsync(row.Summary.Id);
            Detail = d;
            foreach (var m in d.Messages) Messages.Add(new MessageRow(m));
            foreach (var a in d.Attachments) Attachments.Add(new AttachmentRow(a));
            foreach (var x in d.Diagnostics) SentDiagnostics.Add(x);
            DetailSummary = $"{d.Summary.Reference} · {SupportStatusText.Label(d.Summary.Status)} · created {d.Summary.CreatedAt.LocalDateTime:g} · " +
                            (d.Mode == SubmissionMode.DiagnosticsOnly ? "diagnostics only" : "diagnostics and files") + "\n" +
                            $"Goal: {d.Details.Goal}\nProblem: {d.Details.Problem}" + (d.RetentionNote is { } n ? "\n" + n : "");
            CanReply = d.Summary.Status is SupportStatus.Submitted or SupportStatus.UnderReview or SupportStatus.MoreInformationNeeded or SupportStatus.InProgress;
            CanWithdraw = d.Summary.Status is not (SupportStatus.Closed or SupportStatus.Completed);
        }
        catch (HcgApiException ex) { ErrorMessage = ex.Message; }
    }

    private bool CanSend() => CanReply && Reply.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendReplyAsync()
    {
        if (Detail is null) return;
        try
        {
            await support.ReplyAsync(Detail.Summary.Id, Reply);
            Reply = "";
            await LoadAsync();
        }
        catch (HcgApiException ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task WithdrawAsync()
    {
        if (Detail is null) return;
        if (!await dialogs.ConfirmAsync("Withdraw request", $"Withdraw {Detail.Summary.Reference}? It is closed and its attachments are deleted from HCG now.", "Withdraw", destructive: true)) return;
        try
        {
            await support.WithdrawAsync(Detail.Summary.Id);
            await LoadAsync();
        }
        catch (HcgApiException ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task DownloadAsync(AttachmentRow row)
    {
        if (Detail is null) return;
        try
        {
            var link = await support.DownloadLinkAsync(Detail.Summary.Id, row.Attachment.Id);
            await shell.OpenUrlAsync(link.Url.ToString());
        }
        catch (HcgApiException ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand] private void NewRequest() => navigation.Navigate("support");
    [RelayCommand] private void OpenAccount() => navigation.Navigate("account");
}
