using System.Collections.ObjectModel;
using System.IO;
using Pusher.App.Mvvm;
using Pusher.Core.Abstractions;
using Pusher.Core.Models;
using Pusher.Core.Scripting;

namespace Pusher.App.ViewModels;

/// <summary>
/// Edits the stored PushScript of an activated project. On save the script is
/// recompiled and re-planned with the project's original seed; every still-Pending
/// commit is replaced by the new plan while executed history (Pushed/Failed/Skipped)
/// is preserved untouched. New files referenced by the plan are copied into the
/// staging snapshot so future commits can include them.
/// </summary>
public sealed class EditScriptViewModel : ObservableObject
{
    private readonly IStateStore _store;
    private readonly Project _project;

    private string _scriptText;
    private string _statusMessage = "";
    private bool _hasErrors;

    public ObservableCollection<IssueRow> Issues { get; } = [];

    public RelayCommand ValidateCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>Raised after a successful save (dialog closes, dashboard refreshes).</summary>
    public event Action? Saved;
    /// <summary>Raised when the user cancels (dialog closes, nothing changed).</summary>
    public event Action? Cancelled;

    public string ProjectName => _project.Name;
    public string ScriptText { get => _scriptText; set => SetProperty(ref _scriptText, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool HasErrors { get => _hasErrors; private set => SetProperty(ref _hasErrors, value); }
    public bool HasIssues => Issues.Count > 0;

    public EditScriptViewModel(IStateStore store, Project project)
    {
        _store = store;
        _project = project;
        _scriptText = project.ScriptText;
        Issues.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasIssues));

        ValidateCommand = new RelayCommand(() => Validate());
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => Cancelled?.Invoke());
    }

    /// <summary>Compile + plan with the stored seed; fills the issues list.
    /// Returns the plan when it is usable, null otherwise.</summary>
    private Core.Planning.PlanResult? Validate()
    {
        Issues.Clear();
        var compile = PushScriptEngine.Compile(ScriptText);
        foreach (var d in compile.Diagnostics)
            Issues.Add(new IssueRow(d.Code, d.Severity.ToString(), $"{d.Line}:{d.Column}", d.Message));

        if (compile.Model is null)
        {
            HasErrors = true;
            StatusMessage = "Fix script errors before saving.";
            return null;
        }

        if (!Directory.Exists(_project.LocalPath))
        {
            HasErrors = true;
            StatusMessage = $"Local folder not found: {_project.LocalPath}";
            return null;
        }

        var files = NewProjectViewModel.ListProjectFiles(_project.LocalPath);
        var plan = PushScriptEngine.Plan(ScriptText, files, _project.ScriptSeed);
        foreach (var d in plan.Diagnostics.Where(d => d.IsError))
            Issues.Add(new IssueRow(d.Code, "Error", $"{d.Line}:{d.Column}", d.Message));

        HasErrors = Issues.Any(i => i.IsError);
        if (!plan.Success || HasErrors)
        {
            StatusMessage = "Planning failed — fix the errors above.";
            return null;
        }

        StatusMessage = $"Script OK — {plan.Commits.Count} commits planned.";
        return plan;
    }

    private void Save()
    {
        var plan = Validate();
        if (plan is null) return;

        // Executed commits stay: only the plan tail beyond the highest executed
        // order is (re)created. A pushed commit can never be rewritten from here.
        var existing = _store.GetCommitPlans(_project.Id);
        int maxDoneOrder = existing.Where(p => p.Status != CommitPlanStatus.Pending)
                                   .Select(p => p.Order).DefaultIfEmpty(0).Max();
        var tail = plan.Commits.Where(c => c.Order > maxDoneOrder).ToList();

        _store.DeletePendingPlans(_project.Id);

        if (tail.Count > 0)
        {
            var newPlans = tail.Select(c => new CommitPlan
            {
                ProjectId = _project.Id,
                Order = c.Order,
                Message = c.Message,
                Body = c.Body,
                FileGlobs = c.Globs,
                ResolvedFiles = c.Files,
                Mode = c.Mode,
            }).ToList();
            _store.SaveCommitPlans(_project.Id, newPlans);

            var slots = tail.Zip(newPlans, (c, cp) => new Slot
            {
                CommitPlanId = cp.Id,
                ScheduledAtUtc = c.ScheduledAt.ToUniversalTime(),
            }).ToList();
            _store.SaveSlots(_project.Id, slots);

            // Refresh the staging snapshot with the files the new tail publishes so
            // commits can include them (the .git folder in staging is untouched).
            if (_project.StagingPath is not null)
                CopyIntoStaging(_project.StagingPath, tail.SelectMany(c => c.Files).Distinct());
        }

        var compile = PushScriptEngine.Compile(ScriptText);
        _project.ScriptText = ScriptText;
        if (compile.Model is not null) _project.Branch = compile.Model.Repo.Branch;
        _store.UpsertProject(_project);

        StatusMessage = tail.Count > 0
            ? $"Saved — {tail.Count} pending commit(s) re-planned."
            : "Saved — script updated (no pending commits to re-plan).";
        Saved?.Invoke();
    }

    private void CopyIntoStaging(string stagingPath, IEnumerable<string> relativeFiles)
    {
        Directory.CreateDirectory(stagingPath);
        foreach (var rel in relativeFiles)
        {
            var src = Path.Combine(_project.LocalPath, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(stagingPath, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
    }
}
