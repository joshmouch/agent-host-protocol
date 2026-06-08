// AHP Conformance Runner — Kotlin/JVM build-phase B5.
//
// Standalone Gradle project that drives the real Kotlin client's reducers
// against the scenario-driven host over a REAL WebSocket. No mocks.
//
// Depends on the in-repo Kotlin client as a file dependency so it uses
// exactly the same reducer/types/KSerializer code that will ship to users.
// Uses Java 11's built-in java.net.http.WebSocket — zero extra runtime deps.

import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    kotlin("jvm") version "2.3.21"
    kotlin("plugin.serialization") version "2.3.21"
}

group = "com.microsoft.agenthostprotocol.conformance"
version = "0.0.0"

kotlin {
    jvmToolchain(17)
    compilerOptions {
        jvmTarget = JvmTarget.JVM_11
    }
}

java {
    targetCompatibility = JavaVersion.VERSION_11
    sourceCompatibility = JavaVersion.VERSION_11
}


dependencies {
    // The REAL in-repo Kotlin client — reducers, types, KSerializer.
    // This is a fat-jar dependency via the compiled classes of the sibling
    // client project. We use a local file dep on the built JAR so this
    // project compiles standalone without a multi-project Gradle build.
    implementation(files("../../clients/kotlin/build/libs/agent-host-protocol-0.2.0.jar"))

    // kotlinx.serialization JSON (same version as the client).
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.11.0")

    // JUnit 5 for @TestFactory scenario-driven tests.
    testImplementation("org.junit.jupiter:junit-jupiter:5.13.0")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher:1.13.0")
}

tasks.withType<Test>().configureEach {
    useJUnitPlatform()
    testLogging {
        events("passed", "skipped", "failed")
        showStandardStreams = true
    }
    // Absolute path of the scenario-driven host script (B3).
    systemProperty(
        "ahp.scenarioHostScript",
        rootProject.projectDir
            .resolve("../../conformance/host/scenario-host.mjs")
            .canonicalPath,
    )
    // Root directory of the scenario corpus.
    systemProperty(
        "ahp.scenariosRoot",
        rootProject.projectDir
            .resolve("../../types/test-cases/scenarios")
            .canonicalPath,
    )
    // Node.js executable — use PATH by default; override if needed.
    systemProperty(
        "ahp.nodeExecutable",
        System.getProperty("ahp.nodeExecutable") ?: "node",
    )
    // Which tranche to run. Options: "brief" (default), "full".
    // "brief" = all 23 round-trips + 30 reducer sample + all 46 negatives = 99.
    // "full"  = all 233 scenarios (23 round-trips + 164 reducers + 46 negatives).
    systemProperty(
        "ahp.tranche",
        System.getProperty("ahp.tranche") ?: "brief",
    )
    // Timeout per scenario in milliseconds (host + ws protocol).
    systemProperty(
        "ahp.scenarioTimeoutMs",
        System.getProperty("ahp.scenarioTimeoutMs") ?: "10000",
    )
}
