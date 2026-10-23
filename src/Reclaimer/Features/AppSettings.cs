namespace Reclaimer.Shell
{
    // Reclaimer's own settings fields (07 §5): same partial class and namespace as Shell/Settings.cs
    public sealed partial class Settings
    {
        public List<string> ProjectRoots { get; set; } = Features.Defaults.ProjectRoots();
        public int AgeDays { get; set; } = 90;           // "abandoned" threshold for "Mark recommended"
        public int QuarantineDays { get; set; } = 7;     // 1–30
        public int MinSizeMb { get; set; } = 50;
        public List<string> ModelDirs { get; set; } = Features.Defaults.ModelDirs();
        public List<string> ExtraCacheDirs { get; set; } = [];
        public List<string> Excluded { get; set; } = [];
        public List<string> LauncherDirs { get; set; } = [];     // v1.0: scripts and configs that start the user's models; a model they name is in use
        public List<string> QuarantineRoots { get; set; } = [];  // roots made on other volumes, for Reconcile and the list
        public DateTime? LastScanUtc { get; set; }
        public bool RemindWeekly { get; set; }           // v1.0: "≈N GB can be reclaimed" once a week, off by default
        public DateTime? LastReminderUtc { get; set; }
    }
}

namespace Reclaimer.Features
{
    public static class Defaults
    {
        /// <summary>Debug: only the sandbox (07 §9). Release: the user picks folders on the Overview page.</summary>
        public static List<string> ProjectRoots() =>
#if DEBUG
            [System.IO.Path.Combine(Quarantine.SandboxDir, "projects")];
#else
            [];
#endif

        public static List<string> ModelDirs() =>
#if DEBUG
            [System.IO.Path.Combine(Quarantine.SandboxDir, "models")];
#else
            [];
#endif
    }
}
