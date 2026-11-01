package com.hakmicapital.hello;

import android.app.Activity;
import android.os.Bundle;
import android.widget.TextView;

/** Minimal activity used by HCG Executable Maker's Android integration tests. No third-party dependencies. */
public final class MainActivity extends Activity {
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        TextView text = new TextView(this);
        text.setText("Built by HCG Executable Maker");
        setContentView(text);
    }
}
