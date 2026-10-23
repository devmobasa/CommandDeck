namespace CommandDeck;

internal static class ProjectMarkerNames
{
    internal const string PackageJson = "package.json";
    internal const string CargoToml = "Cargo.toml";
    internal const string PomXml = "pom.xml";
    internal const string ComposerJson = "composer.json";
    internal const string BuildGradle = "build.gradle";
    internal const string BuildGradleKotlin = "build.gradle.kts";
    internal const string SettingsGradle = "settings.gradle";
    internal const string SettingsGradleKotlin = "settings.gradle.kts";
    internal const string PyProjectToml = "pyproject.toml";
    internal const string SetupPy = "setup.py";
    internal const string SetupCfg = "setup.cfg";
    internal const string RequirementsTxt = "requirements.txt";
    internal const string Pipfile = "Pipfile";

    internal static bool IsMarker( string name ) =>
        name is PackageJson or CargoToml or PomXml or ComposerJson || IsGradleMarker( name ) || IsPythonMarker( name );

    internal static bool IsGradleMarker( string name ) =>
        name is BuildGradle or BuildGradleKotlin or SettingsGradle or SettingsGradleKotlin;

    internal static bool IsPythonMarker( string name ) =>
        name is PyProjectToml or SetupPy or SetupCfg or RequirementsTxt or Pipfile;
}
