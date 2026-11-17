plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// 다른 이름으로 앱을 내려면 이 세 값과 서명 키만 바꿉니다(설계 15.3).
val gvrApplicationId = "kr.co.seeplay.gvr"
val gvrScheme = "seeplay-gvr"
val gvrVersionCode = 3
val gvrVersionName = "0.1.2"

android {
    namespace = "kr.co.seeplay.gvr"   // 코드 패키지(식별자와 별개)
    compileSdk = 36
    buildToolsVersion = "36.0.0"

    defaultConfig {
        applicationId = gvrApplicationId
        minSdk = 26
        targetSdk = 36
        versionCode = gvrVersionCode
        versionName = gvrVersionName
        manifestPlaceholders["gvrScheme"] = gvrScheme
        buildConfigField("String", "SCHEME", "\"$gvrScheme\"")
        buildConfigField("boolean", "DIAG", "true")
        ndk { abiFilters += listOf("arm64-v8a", "armeabi-v7a") }
    }

    signingConfigs {
        create("proto") {
            storeFile = file("../keystore/gvr-proto.jks")
            storePassword = "gvrproto"
            keyAlias = "gvr"
            keyPassword = "gvrproto"
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
