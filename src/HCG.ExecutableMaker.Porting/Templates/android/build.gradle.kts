// Root build script: declares the Android Gradle Plugin version without applying it.
plugins {
    alias(libs.plugins.android.application) apply false
}
