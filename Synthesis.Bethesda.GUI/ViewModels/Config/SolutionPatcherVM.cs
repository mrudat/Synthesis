using DynamicData;
using Microsoft.WindowsAPICodePack.Dialogs;
using Synthesis.Bethesda.Execution.Settings;
using Noggog;
using Noggog.WPF;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Text;
using System.Linq;
using DynamicData.Binding;
using System.Windows.Input;
using System.Diagnostics;
using System.Reactive;
using Newtonsoft.Json;
using Synthesis.Bethesda.DTO;
using Synthesis.Bethesda.Execution.Patchers.Git;
using Synthesis.Bethesda.Execution.Patchers;

namespace Synthesis.Bethesda.GUI
{
    public class SolutionPatcherVM : PatcherVM
    {
        public PathPickerVM SolutionPath { get; } = new PathPickerVM()
        {
            ExistCheckOption = PathPickerVM.CheckOptions.On,
            PathType = PathPickerVM.PathTypeOptions.File,
        };

        public IObservableCollection<string> AvailableProjects { get; }

        [Reactive]
        public string ProjectSubpath { get; set; } = string.Empty;

        public PathPickerVM SelectedProjectPath { get; } = new PathPickerVM()
        {
            ExistCheckOption = PathPickerVM.CheckOptions.On,
            PathType = PathPickerVM.PathTypeOptions.File,
        };

        private readonly ObservableAsPropertyHelper<string> _DisplayName;
        public override string DisplayName => _DisplayName.Value;

        private readonly ObservableAsPropertyHelper<ConfigurationState> _State;
        public override ConfigurationState State => _State.Value;

        public ICommand OpenSolutionCommand { get; }

        [Reactive]
        public string ShortDescription { get; set; } = string.Empty;

        [Reactive]
        public string LongDescription { get; set; } = string.Empty;

        [Reactive]
        public bool HiddenByDefault { get; set; }

        [Reactive]
        public PreferredAutoVersioning Versioning { get; set; }

        public ObservableCollectionExtended<PreferredAutoVersioning> VersioningOptions { get; } = new ObservableCollectionExtended<PreferredAutoVersioning>(EnumExt.GetValues<PreferredAutoVersioning>());

        public SolutionPatcherVM(ProfileVM parent, SolutionPatcherSettings? settings = null)
            : base(parent, settings)
        {
            CopyInSettings(settings);
            SolutionPath.Filters.Add(new CommonFileDialogFilter("Solution", ".sln"));
            SelectedProjectPath.Filters.Add(new CommonFileDialogFilter("Project", ".csproj"));

            _DisplayName = Observable.CombineLatest(
                this.WhenAnyValue(x => x.Nickname),
                this.WhenAnyValue(x => x.SelectedProjectPath.TargetPath)
                    .StartWith(settings?.ProjectSubpath ?? string.Empty),
                (nickname, path) =>
                {
                    if (!string.IsNullOrWhiteSpace(nickname)) return nickname;
                    try
                    {
                        var name = Path.GetFileName(Path.GetDirectoryName(path));
                        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
                        return name;
                    }
                    catch (Exception)
                    {
                        return string.Empty;
                    }
                })
                .ToProperty(this, nameof(DisplayName), Nickname);

            AvailableProjects = SolutionPatcherConfigLogic.AvailableProject(
                this.WhenAnyValue(x => x.SolutionPath.TargetPath))
                .ObserveOnGui()
                .ToObservableCollection(this);

            var projPath = SolutionPatcherConfigLogic.ProjectPath(
                solutionPath: this.WhenAnyValue(x => x.SolutionPath.TargetPath),
                projectSubpath: this.WhenAnyValue(x => x.ProjectSubpath));
            projPath
                .Subscribe(p => SelectedProjectPath.TargetPath = p)
                .DisposeWith(this);

            _State = Observable.CombineLatest(
                    this.WhenAnyValue(x => x.SolutionPath.ErrorState),
                    this.WhenAnyValue(x => x.SelectedProjectPath.ErrorState),
                    this.WhenAnyValue(x => x.Profile.Config.MainVM)
                        .Select(x => x.DotNetSdkInstalled)
                        .Switch(),
                    (sln, proj, dotnet) =>
                    {
                        if (sln.Failed) return new ConfigurationState(sln);
                        if (dotnet == null) return new ConfigurationState(ErrorResponse.Fail("No dotnet SDK installed"));
                        return new ConfigurationState(proj);
                    })
                .ToGuiProperty<ConfigurationState>(this, nameof(State), new ConfigurationState(ErrorResponse.Fail("Evaluating"))
                {
                    IsHaltingError = false
                });

            OpenSolutionCommand = ReactiveCommand.Create(
                canExecute: this.WhenAnyValue(x => x.SolutionPath.InError)
                    .Select(x => !x),
                execute: () =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(SolutionPath.TargetPath)
                        {
                            UseShellExecute = true,
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Logger.Error(ex, $"Error opening solution: {SolutionPath.TargetPath}");
                    }
                });

            var metaPath = this.WhenAnyValue(x => x.SelectedProjectPath.TargetPath)
                .Select(projPath =>
                {
                    try
                    {
                        return Path.Combine(Path.GetDirectoryName(projPath)!, Constants.MetaFileName);
                    }
                    catch (Exception)
                    {
                        return string.Empty;
                    }
                })
                .Replay(1)
                .RefCount();

            // Set up meta file sync
            metaPath
                .Select(path =>
                {
                    return Noggog.ObservableExt.WatchFile(path)
                        .StartWith(Unit.Default)
                        .Select(_ =>
                        {
                            try
                            {
                                return JsonConvert.DeserializeObject<PatcherCustomization>(
                                    File.ReadAllText(path),
                                    Execution.Constants.JsonSettings);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error(ex, "Error reading in meta");
                            }
                            return default(PatcherCustomization?);
                        });
                })
                .Switch()
                .DistinctUntilChanged()
                .ObserveOnGui()
                .Subscribe(info =>
                {
                    if (info == null) return;
                    if (info.Nickname != null)
                    {
                        this.Nickname = info.Nickname;
                    }
                    this.LongDescription = info.LongDescription ?? string.Empty;
                    this.ShortDescription = info.OneLineDescription ?? string.Empty;
                    this.HiddenByDefault = info.HideByDefault;
                    this.Versioning = info.PreferredAutoVersioning;
                })
                .DisposeWith(this);

            Observable.CombineLatest(
                    this.WhenAnyValue(x => x.DisplayName),
                    this.WhenAnyValue(x => x.ShortDescription),
                    this.WhenAnyValue(x => x.LongDescription),
                    this.WhenAnyValue(x => x.HiddenByDefault),
                    this.WhenAnyValue(x => x.Versioning),
                    metaPath,
                    (nickname, shortDesc, desc, hidden, versioning, meta) => (nickname, shortDesc, desc, hidden, versioning, meta))
                .DistinctUntilChanged()
                .Throttle(TimeSpan.FromMilliseconds(200), RxApp.MainThreadScheduler)
                .Skip(1)
                .Subscribe(x =>
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(x.meta)) return;
                        File.WriteAllText(x.meta,
                            JsonConvert.SerializeObject(
                                new PatcherCustomization()
                                {
                                    OneLineDescription = x.shortDesc,
                                    LongDescription = x.desc,
                                    HideByDefault = x.hidden,
                                    Nickname = x.nickname,
                                    PreferredAutoVersioning = x.versioning
                                },
                                Formatting.Indented,
                                Execution.Constants.JsonSettings));
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Error writing out meta");
                    }
                })
                .DisposeWith(this);
        }

        public override PatcherSettings Save()
        {
            var ret = new SolutionPatcherSettings();
            CopyOverSave(ret);
            ret.SolutionPath = this.SolutionPath.TargetPath;
            ret.ProjectSubpath = this.ProjectSubpath;
            return ret;
        }

        private void CopyInSettings(SolutionPatcherSettings? settings)
        {
            if (settings == null) return;
            this.SolutionPath.TargetPath = settings.SolutionPath;
            this.ProjectSubpath = settings.ProjectSubpath;
        }

        public override PatcherRunVM ToRunner(PatchersRunVM parent)
        {
            return new PatcherRunVM(
                parent,
                this,
                new SolutionPatcherRun(
                    name: DisplayName,
                    pathToSln: SolutionPath.TargetPath,
                    pathToExtraDataBaseFolder: Execution.Constants.TypicalExtraData,
                    pathToProj: SelectedProjectPath.TargetPath));
        }

        public class SolutionPatcherConfigLogic
        {
            public static IObservable<IChangeSet<string>> AvailableProject(IObservable<string> solutionPath)
            {
                return solutionPath
                    .ObserveOn(RxApp.TaskpoolScheduler)
                    .Select(SolutionPatcherRun.AvailableProjects)
                    .Select(x => x.AsObservableChangeSet())
                    .Switch()
                    .RefCount();
            }

            public static IObservable<string> ProjectPath(IObservable<string> solutionPath, IObservable<string> projectSubpath)
            {
                return projectSubpath
                    // Need to throttle, as bindings flip to null quickly, which we want to skip
                    .Throttle(TimeSpan.FromMilliseconds(150), RxApp.MainThreadScheduler)
                    .DistinctUntilChanged()
                    .CombineLatest(solutionPath.DistinctUntilChanged(),
                        (subPath, slnPath) =>
                        {
                            if (subPath == null || slnPath == null) return string.Empty;
                            try
                            {
                                return Path.Combine(Path.GetDirectoryName(slnPath)!, subPath);
                            }
                            catch (Exception)
                            {
                                return string.Empty;
                            }
                        })
                    .Replay(1)
                    .RefCount();
            }
        }
    }
}
