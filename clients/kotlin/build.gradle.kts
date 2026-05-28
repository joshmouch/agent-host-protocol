import com.vanniktech.maven.publish.JavadocJar
import com.vanniktech.maven.publish.KotlinJvm
import com.vanniktech.maven.publish.SourcesJar
import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    alias(libs.plugins.kotlin.jvm)
    alias(libs.plugins.kotlin.serialization)
    alias(libs.plugins.maven.publish)
}

// `project.group` / `project.version` are also derived from `GROUP` and
// `VERSION_NAME` in `gradle.properties` by the Vanniktech plugin (which
// applies them to the publication coordinates). We mirror them onto the
// project itself so that standard tasks like `./gradlew properties -q` can
// surface the version — which the `publish-kotlin.yml` workflow uses to
// validate the `kotlin/v*` git tag against the source-of-truth version.
group = providers.gradleProperty("GROUP").get()
version = providers.gradleProperty("VERSION_NAME").get()

// Build with JDK 17 but emit Java 8-compatible bytecode so the artifact works
// for Android consumers without forcing core library desugaring or AGP 8.x+.
kotlin {
    jvmToolchain(17)
    compilerOptions {
        jvmTarget = JvmTarget.JVM_1_8
    }
}

java {
    targetCompatibility = JavaVersion.VERSION_1_8
    sourceCompatibility = JavaVersion.VERSION_1_8
}

dependencies {
    api(libs.kotlinx.serialization.json)

    testImplementation(libs.kotlin.test)
    testImplementation(libs.junit.jupiter)
    testRuntimeOnly(libs.junit.platform.launcher)
}

tasks.withType<Test>().configureEach {
    useJUnitPlatform()
    testLogging {
        events("passed", "skipped", "failed")
        showStandardStreams = false
    }
    // Pass the absolute path of the shared reducer test fixtures to the JVM
    // running the tests so `FixtureDrivenReducerTest` can load them without
    // depending on the current working directory (which varies between
    // Gradle CLI, IDE runners, and CI).
    systemProperty(
        "ahp.reducerFixturesDir",
        rootProject.projectDir
            .resolve("../../types/test-cases/reducers")
            .canonicalPath,
    )
}

mavenPublishing {
    // `automaticRelease = true` makes `publishAndReleaseToMavenCentral` close
    // and promote the staging deployment in one step (no manual Sonatype UI).
    // The plugin only targets the Sonatype Central Portal in 0.36+, so the
    // `SonatypeHost` parameter has been removed from the public API.
    publishToMavenCentral(automaticRelease = true)
    signAllPublications()

    configure(
        KotlinJvm(
            // No real Javadoc for now (KDoc is on the source jar). Maven
            // Central requires a `-javadoc` jar even when empty.
            javadocJar = JavadocJar.Empty(),
            sourcesJar = SourcesJar.Sources(),
        ),
    )

    // `name`, `description`, `url`, `inceptionYear`, license, SCM, developers,
    // and issueManagement are all populated automatically by the plugin from
    // the `POM_*` keys in `gradle.properties` — we don't repeat them here
    // (doing so causes duplicate <license>/<developer> entries in the POM).
}


