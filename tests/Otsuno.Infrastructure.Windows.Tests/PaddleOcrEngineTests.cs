using System.Diagnostics;
using Otsuno.Infrastructure.Windows.Ocr;

namespace Otsuno.Infrastructure.Windows.Tests;

public class PaddleOcrEngineTests {
    [Fact]
    public void EnsureDependenciesInstalledUsesConfiguredPythonWhenPaddleOcrExists() {
        var engine = new TestPaddleOcrEngine("global-python", "venv-python") {
            DependencyResults = {
                ["global-python"] = true
            }
        };

        engine.EnsureDependencies();

        Assert.Equal("global-python", engine.ActivePythonPath);
        Assert.False(engine.DedicatedEnvironmentEnsured);
        Assert.Empty(engine.InstallTargets);
    }

    [Fact]
    public void EnsureDependenciesInstalledUsesDedicatedVenvWhenGlobalPaddleOcrIsMissing() {
        var engine = new TestPaddleOcrEngine("global-python", "venv-python") {
            DependencyResults = {
                ["global-python"] = false,
                ["venv-python"] = true
            }
        };

        engine.EnsureDependencies();

        Assert.Equal("venv-python", engine.ActivePythonPath);
        Assert.True(engine.DedicatedEnvironmentEnsured);
        Assert.Empty(engine.InstallTargets);
    }

    [Fact]
    public void EnsureDependenciesInstalledInstallsPaddleOcrIntoDedicatedVenvWhenMissing() {
        var engine = new TestPaddleOcrEngine("global-python", "venv-python") {
            DependencyResults = {
                ["global-python"] = false,
                ["venv-python"] = false
            }
        };

        engine.EnsureDependencies();

        Assert.Equal("venv-python", engine.ActivePythonPath);
        Assert.Equal(["venv-python"], engine.InstallTargets);
    }

    [Fact]
    public void EnsureDependenciesInstalledReportsPaddleOcrSetupStatus() {
        var messages = new List<string>();
        var engine = new TestPaddleOcrEngine("global-python", "venv-python") {
            DependencyResults = {
                ["global-python"] = false,
                ["venv-python"] = true
            }
        };
        engine.StatusChanged += (_, e) => messages.Add(e.Message);

        engine.EnsureDependencies();

        Assert.Contains(messages, message => message.Contains("Checking PaddleOCR dependencies"));
        Assert.Contains(messages, message => message.Contains("not found"));
        Assert.Contains(messages, message => message.Contains("ready"));
    }

    [Fact]
    public void InstallPaddleOcrDependenciesReportsPackageInstallStatus() {
        var messages = new List<string>();
        var engine = new CommandStubPaddleOcrEngine();
        engine.StatusChanged += (_, e) => messages.Add(e.Message);

        engine.InstallDependencies("venv-python");

        Assert.Contains(messages, message => message.Contains("Installing Python packaging tools"));
        Assert.Contains(messages, message => message.Contains("Python packaging tools is installed"));
        Assert.Contains(messages, message => message.Contains("Installing PaddlePaddle"));
        Assert.Contains(messages, message => message.Contains("PaddlePaddle is installed"));
        Assert.Contains(messages, message => message.Contains("Installing PaddleOCR"));
        Assert.Contains(messages, message => message.Contains("PaddleOCR is installed"));
    }

    [Fact]
    public void ConfigurePythonEnvironmentForcesUtf8Output() {
        var engine = new CommandStubPaddleOcrEngine();
        var startInfo = new ProcessStartInfo();

        engine.ConfigureEnvironment(startInfo);

        Assert.Equal("1", startInfo.Environment["PYTHONUTF8"]);
        Assert.Equal("utf-8", startInfo.Environment["PYTHONIOENCODING"]);
    }

    [Fact]
    public void ReportLanguageLoadPlanReportsSingleLanguageModel() {
        var messages = new List<string>();
        var engine = new LanguagePlanPaddleOcrEngine("en", "ja");
        engine.StatusChanged += (_, e) => messages.Add(e.Message);

        engine.ReportPlan();

        Assert.Contains(messages, message => message.Contains("one language model: en"));
    }

    [Fact]
    public void ReportLanguageLoadPlanReportsDetectLanguageModelCount() {
        var messages = new List<string>();
        var engine = new LanguagePlanPaddleOcrEngine("Detect language", "ja");
        engine.StatusChanged += (_, e) => messages.Add(e.Message);

        engine.ReportPlan();

        Assert.Contains(messages, message => message.Contains("detect-language mode will load 4 language models"));
        Assert.Contains(messages, message => message.Contains("Select a specific Source language"));
    }

    protected class TestPaddleOcrEngine(string pythonPath, string dedicatedPythonPath) : PaddleOcrEngine("en", "ja", pythonPath, "bridge.py") {
        public Dictionary<string, bool> DependencyResults { get; } = [];
        public List<string> InstallTargets { get; } = [];
        public bool DedicatedEnvironmentEnsured { get; protected set; }
        public string ActivePythonPath => activePythonPath;

        public void EnsureDependencies() {
            EnsureDependenciesInstalled();
        }

        protected override bool HasPaddleOcrDependencies(string executablePath) {
            return DependencyResults.TryGetValue(executablePath, out var result) && result;
        }

        protected override string EnsureDedicatedPythonEnvironment() {
            DedicatedEnvironmentEnsured = true;
            return dedicatedPythonPath;
        }

        protected override void InstallPaddleOcrDependencies(string executablePath) {
            InstallTargets.Add(executablePath);
            DependencyResults[executablePath] = true;
        }
    }

    protected class CommandStubPaddleOcrEngine() : PaddleOcrEngine("en", "ja", "python", "bridge.py") {
        public void InstallDependencies(string executablePath) {
            InstallPaddleOcrDependencies(executablePath);
        }

        public void ConfigureEnvironment(ProcessStartInfo startInfo) {
            ConfigurePythonEnvironment(startInfo);
        }

        protected override ProcessResult RunPythonCommand(string executablePath, string arguments, TimeSpan? timeout = null) {
            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }

    protected class LanguagePlanPaddleOcrEngine(string sourceLanguage, string targetLanguage) : PaddleOcrEngine(sourceLanguage, targetLanguage, "python", "bridge.py") {
        public void ReportPlan() {
            ReportLanguageLoadPlan();
        }
    }
}
