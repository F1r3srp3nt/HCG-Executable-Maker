plugins {
    alias(libs.plugins.android.application)
}

android {
    namespace = "com.hakmicapital.hello"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.hakmicapital.hello"
        minSdk = 24
        targetSdk = 35
        versionCode = 3
        versionName = "1.2.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}
