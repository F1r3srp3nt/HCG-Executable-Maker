#include <gtk/gtk.h>

static void on_click(GtkWidget *button, gpointer label) {
    gtk_label_set_text(GTK_LABEL(label), "Clicked!");
}

int main(int argc, char **argv) {
    gtk_init(&argc, &argv);
    GtkWidget *window = gtk_window_new(GTK_WINDOW_TOPLEVEL);
    GtkWidget *box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 8);
    GtkWidget *label = gtk_label_new("Hello from GTK");
    GtkWidget *button = gtk_button_new_with_label("Click");
    g_signal_connect(button, "clicked", G_CALLBACK(on_click), label);
    g_signal_connect(window, "destroy", G_CALLBACK(gtk_main_quit), NULL);
    gtk_container_add(GTK_CONTAINER(box), label);
    gtk_container_add(GTK_CONTAINER(box), button);
    gtk_container_add(GTK_CONTAINER(window), box);
    gtk_widget_show_all(window);
    gtk_main();
    return 0;
}
