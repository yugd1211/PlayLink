import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// 다른 이름으로 앱을 내려면 이 세 값과 서명 키만 바꿉니다(설계 15.3).
val appId = "io.playlink.app"
val appScheme = "playlink"
val appVersionCode = 3
val appVersionName = "0.1.2"

val keystoreFile = rootProject.file("keystore.properties")
val keystoreProps = Properties().apply { if (keystoreFile.exists()) keystoreFile.inputStream().use { load(it) } }

android {
    namespace = "io.playlink.app"   // 코드 패키지(식별자와 별개)
    compileSdk = 36
    buildToolsVersion = "36.0.0"

    defaultConfig {
        applicationId = appId
        minSdk = 26
        targetSdk = 36
        versionCode = appVersionCode
        versionName = appVersionName
        manifestPlaceholders["appScheme"] = appScheme
        buildConfigField("String", "SCHEME", "\"$appScheme\"")
        buildConfigField("boolean", "DIAG", "true")
        ndk { abiFilters += listOf("arm64-v8a", "armeabi-v7a") }
    }

    // 서명 키와 비밀번호는 저장소에 넣지 않습니다. android/keystore.properties(.gitignore)에서 읽고,
    // 파일이 없으면 이 PC 의 안드로이드 디버그 키로 서명합니다(PC 마다 서명이 달라 서로 덮어 설치되지 않습니다).
    //   storeFile=<jks 경로>  storePassword=…  keyAlias=…  keyPassword=…
    signingConfigs {
        create("proto") {
            if (keystoreFile.exists()) {
                storeFile = rootProject.file(keystoreProps.getProperty("storeFile"))
                storePassword = keystoreProps.getProperty("storePassword")
                keyAlias = keystoreProps.getProperty("keyAlias")
                keyPassword = keystoreProps.getProperty("keyPassword")
            } else {
                initWith(getByName("debug"))
            }
        }
    }

    buildTypes {
        getByName("debug") {
            signingConfig = signingConfigs.getByName("proto")
        }
        getByName("release") {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("proto")
            buildConfigField("boolean", "DIAG", "false")
        }
        // 배포와 측정에 쓰는 빌드: 디버그 불가, 진단 기능 켬(브리프 3.3의 3)
        create("proto") {
            initWith(getByName("release"))
            isDebuggable = false
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("proto")
            buildConfigField("boolean", "DIAG", "true")
            matchingFallbacks += listOf("release")
        }
    }

    buildFeatures { buildConfig = true }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    lint { checkReleaseBuilds = false; abortOnError = false }
}

kotlin { compilerOptions { jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17) } }

dependencies {
    implementation("androidx.activity:activity-ktx:1.9.3")
    val camerax = "1.4.2"
    implementation("androidx.camera:camera-core:$camerax")
    implementation("androidx.camera:camera-camera2:$camerax")
    implementation("androidx.camera:camera-lifecycle:$camerax")
    implementation("androidx.camera:camera-view:$camerax")
    implementation("com.google.zxing:core:3.5.3")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
}
