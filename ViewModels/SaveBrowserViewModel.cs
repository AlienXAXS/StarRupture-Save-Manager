using StarRuptureSaveFixer.Fixers;
using StarRuptureSaveFixer.Models;
using StarRuptureSaveFixer.Services;
using StarRuptureSaveFixer.Utils;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace StarRuptureSaveFixer.ViewModels;

public class SaveBrowserViewModel : ViewModelBase
{
    private readonly SessionManager _sessionManager;
    private readonly SaveFileService _saveFileService;

    private SaveSession? _selectedSession;
    private SaveFileInfo? _selectedSaveFile;
    private string _logOutput = "";
    private bool _isProcessing;
    private int _selectedFixerIndex;

    public SaveBrowserViewModel(string? customPath = null)
    {
        _sessionManager = new SessionManager();
        _sessionManager.CustomSavePath = customPath;
        _saveFileService = new SaveFileService();

        Sessions = new ObservableCollection<SaveSession>();
        Fixers = new ObservableCollection<FixerOption>
        {
            new FixerOption("Fix Drones (Remove invalid targets)", () => new DroneFixer()),
            new FixerOption("Remove All Drones", () => new DroneRemover()),
            new FixerOption("Fix Junctions (3-way & 5-way multi-rail)", () => new JunctionFixer())
        };

        RefreshCommand = new RelayCommand(Refresh);
        FixSelectedCommand = new AsyncRelayCommand(FixSelectedAsync, () => CanFixSelected);
        OpenInExplorerCommand = new RelayCommand(OpenInExplorer, () => SelectedSession != null);

        // Subscribe to logger
        ConsoleLogger.ProgressLogged += OnProgressLogged;
        ConsoleLogger.MessageLogged += OnMessageLogged;

        // Load sessions on initialization
        Refresh();
        LogMessage("Welcome to Star Rupture Save File Manager!", "Info");
        LogMessage("Select a session and save file to get started.", "Info");
    }

    private void OnProgressLogged(string message)
    {
        // Update LogOutput or status processing field
        LogMessage(message, "Info");
    }

    private void OnMessageLogged(string level, string message)
    {
        // Surface messages in the log
        LogMessage(message, level == "PROGRESS" ? "Info" : level);
    }

    public void UpdateCustomPath(string? customPath)
    {
        _sessionManager.CustomSavePath = customPath;
    }

    public ObservableCollection<SaveSession> Sessions { get; }
    public ObservableCollection<FixerOption> Fixers { get; }

    public SaveSession? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetProperty(ref _selectedSession, value))
            {
                SelectedSaveFile = null;
                OnPropertyChanged(nameof(SaveFilesInSession));
            }
        }
    }

    public SaveFileInfo? SelectedSaveFile
    {
        get => _selectedSaveFile;
        set
        {
            if (SetProperty(ref _selectedSaveFile, value))
            {
                OnPropertyChanged(nameof(CanFixSelected));
            }
        }
    }

    public IEnumerable<SaveFileInfo> SaveFilesInSession =>
        SelectedSession?.SaveFiles ?? Enumerable.Empty<SaveFileInfo>();

    public int SelectedFixerIndex
    {
        get => _selectedFixerIndex;
        set => SetProperty(ref _selectedFixerIndex, value);
    }

    public string LogOutput
    {
        get => _logOutput;
        set => SetProperty(ref _logOutput, value);
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                OnPropertyChanged(nameof(CanFixSelected));
            }
        }
    }

    public bool CanFixSelected => !IsProcessing && SelectedSaveFile != null && !SelectedSaveFile.IsBackup;

    public ICommand RefreshCommand { get; }
    public ICommand FixSelectedCommand { get; }
    public ICommand OpenInExplorerCommand { get; }

    public void Refresh()
    {
        var previousSelection = SelectedSession?.FullPath;
        Sessions.Clear();

        foreach (var session in _sessionManager.GetAllSessions())
        {
            Sessions.Add(session);
        }

        // Try to restore selection
        if (previousSelection != null)
        {
            SelectedSession = Sessions.FirstOrDefault(s => s.FullPath == previousSelection);
        }

        if (Sessions.Count == 0)
        {
            LogMessage("No save sessions found. Check your Steam installation.", "Warning");
        }
        else
        {
            LogMessage($"Found {Sessions.Count} session(s) with saves.", "Info");
        }
    }

    private async Task FixSelectedAsync()
    {
        if (SelectedSaveFile == null)
            return;

        if (SelectedFixerIndex < 0 || SelectedFixerIndex >= Fixers.Count)
            return;

        var filePath = SelectedSaveFile.FullPath;
        var fixerOption = Fixers[SelectedFixerIndex];

        IsProcessing = true;
        LogOutput = ""; // Clear log

        try
        {
            LogMessage("Starting save file processing...", "Info");
            LogMessage($"Loading save file: {filePath}", "Info");

            var result = await Task.Run(() =>
            {
                var saveFile = _saveFileService.LoadSaveFile(filePath);
                return (SaveFile: saveFile, Success: true, Error: (string?)null);
            });

            if (!result.Success || result.SaveFile == null)
            {
                LogMessage($"Failed to load save file: {result.Error}", "Error");
                return;
            }

            LogMessage("Save file loaded successfully!", "Success");
            LogMessage($"JSON Size: {result.SaveFile.JsonContent.Length:N0} bytes", "Info");

            var fixer = fixerOption.CreateFixer();
            LogMessage($"Applying fix: {fixerOption.Name}", "Info");

            bool changed = await Task.Run(() => fixer.ApplyFix(result.SaveFile));

            if (changed)
            {
                string backupPath = GetBackupFilePath(filePath);

                LogMessage($"Backing up original file to: {backupPath}", "Info");
                File.Move(filePath, backupPath, overwrite: true);

                LogMessage($"Saving fixed save file to: {filePath}", "Info");
                await Task.Run(() => _saveFileService.SaveSaveFile(result.SaveFile, filePath));

                LogMessage("Save file fixed successfully!", "Success");
                LogMessage($"Original file backed up to: {backupPath}", "Info");

                MessageBox.Show(
                    $"Save file fixed successfully!\n\nFixed file saved to:\n{filePath}\n\nOriginal backed up to:\n{backupPath}",
                    "Success",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                // Refresh to show the new backup file
                Refresh();
            }
            else
            {
                LogMessage("No changes were made to the save file.", "Warning");
                MessageBox.Show(
                    "No changes were needed for this save file.",
                    "No Changes",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (FileNotFoundException ex)
        {
            LogMessage(ex.Message, "Error");
            MessageBox.Show(ex.Message, "File Not Found", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (InvalidDataException ex)
        {
            LogMessage(ex.Message, "Error");
            MessageBox.Show(ex.Message, "Invalid Data", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            LogMessage($"Unexpected error: {ex.Message}", "Error");
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private void OpenInExplorer()
    {
        if (SelectedSession == null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = SelectedSession.FullPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LogMessage($"Failed to open folder: {ex.Message}", "Error");
        }
    }

    private void LogMessage(string message, string type)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string prefix = type switch
        {
            "Error" => "[ERROR]",
            "Warning" => "[WARN]",
            "Success" => "[OK]",
            _ => "[INFO]"
        };

        LogOutput += $"[{timestamp}] {prefix} {message}\n";
    }

    private string GetBackupFilePath(string originalPath)
    {
        string directory = Path.GetDirectoryName(originalPath) ?? "";
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(originalPath);
        string extension = Path.GetExtension(originalPath);

        return Path.Combine(directory, $"{fileNameWithoutExtension}_original{extension}");
    }
}

public class FixerOption
{
    public string Name { get; }
    private readonly Func<IFixer> _factory;

    public FixerOption(string name, Func<IFixer> factory)
    {
        Name = name;
        _factory = factory;
    }

    public IFixer CreateFixer() => _factory();

    public override string ToString() => Name;
}
