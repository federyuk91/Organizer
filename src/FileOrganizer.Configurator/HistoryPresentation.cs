using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using FileOrganizer.Windows;

namespace FileOrganizer.Configurator;

public sealed record HistoryUndoIssue(string FileName, string Reason);

public sealed class HistoryDisplayConverter : IValueConverter
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    public static string StateName(string state) => state switch
    {
        "Committed" => "Spostato", "Undone" => "Ripristinato", "Prepared" => "Da verificare",
        "UndoPrepared" => "Ripristino incompleto", "Aborted" => "Non completato", _ => state
    };
    public static string StateLabel(string state) => (state switch { "Committed" => "✓ ", "Undone" => "↶ ", "Prepared" or "UndoPrepared" => "! ", _ => "— " }) + StateName(state);
    public static string UndoReason(string reason) => reason switch
    {
        "Original path is occupied; nothing overwritten" => "Il percorso originale è occupato; nessun file è stato sovrascritto.",
        "Destination file was replaced or modified" => "Il file di destinazione è stato sostituito o modificato.",
        "Destination content changed; nothing moved" => "Il contenuto del file è cambiato; nessun file è stato spostato.",
        "This movement is already undone or unavailable" => "Lo spostamento è già ripristinato o non è disponibile.",
        "Movement is unavailable or older than seven days" => "Lo spostamento non è disponibile o risale a più di sette giorni fa.",
        "Movement belongs to another configuration" => "Lo spostamento appartiene a un’altra configurazione.",
        "Interrupted undo cannot be verified" => "Non è possibile verificare il ripristino interrotto.",
        "Unsafe undo path" => "Il percorso non soddisfa i controlli di sicurezza.",
        _ => reason
    };
    public static string ShortFolder(string path)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return Path.GetRelativePath(profile, path);
        string[] parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 3 ? parts[0] + Path.DirectorySeparatorChar + "…" + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, parts[^2..]) : path;
    }
    public static string SizeLabel(long bytes)
    {
        double size = bytes; string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return size.ToString(unit == 0 ? "N0" : "0.#", Italian) + " " + units[unit];
    }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (parameter as string) switch
    {
        "Time" when value is DateTimeOffset time => time.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Italian),
        "Size" when value is long bytes => SizeLabel(bytes),
        "Name" when value is string path => Path.GetFileName(path),
        "Folder" when value is string path => ShortFolder(Path.GetDirectoryName(path) ?? path),
        "FolderOption" when value is string path => ShortFolder(path),
        "State" when value is string state => StateLabel(state),
        "DetailHeight" when value is double height => Math.Max(0, Math.Min(150, height * 0.4)),
        _ => value ?? ""
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed partial class EditorViewModel
{
    public static string[] HistoryPeriods { get; } = ["Ultimi 7 giorni", "Oggi", "Ieri", "Ultimi 3 giorni"];
    public static string[] HistoryStates { get; } = ["Tutti gli stati", "Spostato", "Ripristinato", "Da verificare", "Ripristino incompleto"];
    private string historySearch = "", historyPeriod = HistoryPeriods[0], historyState = HistoryStates[0], historyFolder = "Tutte le cartelle";
    private ListCollectionView? historyView;
    private DateTime historyFilterToday;
    private IReadOnlyList<string> historyFolders = ["Tutte le cartelle"];
    private MoveRecord? selectedHistoryEntry;
    public string HistorySearch { get => historySearch; set { if (historySearch == value) return; historySearch = value; Notify(); FilterHistory(); } }
    public string HistoryPeriod { get => historyPeriod; set { if (value is null || historyPeriod == value) return; historyPeriod = value; Notify(); FilterHistory(); } }
    public string HistoryState { get => historyState; set { if (value is null || historyState == value) return; historyState = value; Notify(); FilterHistory(); } }
    public string HistoryFolder { get => historyFolder; set { if (value is null || historyFolder == value) return; historyFolder = value; Notify(); FilterHistory(); } }
    public IReadOnlyList<string> HistoryFolders => historyFolders;
    public ICollectionView HistoryView => historyView ??= CreateHistoryView();
    public string HistoryCount => $"{(historyView ??= CreateHistoryView()).Count} di {historyEntries.Count} spostamenti negli ultimi 7 giorni";
    public bool HasHistorySelection => selectedHistoryIds.Length > 0;
    public string HistorySelectionSummary => selectedHistoryIds.Length switch { 0 => "Nessuno spostamento selezionato", 1 => "1 spostamento selezionato", _ => $"{selectedHistoryIds.Length} spostamenti selezionati" };
    public MoveRecord? SelectedHistoryEntry => selectedHistoryEntry;
    public IReadOnlyList<HistoryUndoIssue> LastUndoIssues { get; private set; } = [];
    public string UndoSummary { get; private set; } = "";
    public ICommand CopyHistorySourceCommand { get; private set; } = null!;
    public ICommand CopyHistoryDestinationCommand { get; private set; } = null!;
    public ICommand CopyHistoryConfigCommand { get; private set; } = null!;
    public ICommand OpenHistoryDestinationCommand { get; private set; } = null!;
    private void InitializeHistoryPresentation()
    {
        CopyHistorySourceCommand = new EditorCommand(() => HistoryAction(() => Clipboard.SetText(SelectedHistoryEntry!.Source)), () => !Busy && SelectedHistoryEntry is not null);
        CopyHistoryDestinationCommand = new EditorCommand(() => HistoryAction(() => Clipboard.SetText(SelectedHistoryEntry!.Destination)), () => !Busy && SelectedHistoryEntry is not null);
        CopyHistoryConfigCommand = new EditorCommand(() => HistoryAction(() => Clipboard.SetText(SelectedHistoryEntry!.ConfigPath)), () => !Busy && SelectedHistoryEntry is not null);
        OpenHistoryDestinationCommand = new EditorCommand(() => HistoryAction(() =>
        {
            string directory = Path.GetDirectoryName(SelectedHistoryEntry!.Destination) ?? throw new IOException("Cartella di destinazione non valida.");
            FileOrganizer.Core.Paths.NoReparse(directory);
            if (!Directory.Exists(directory)) throw new IOException("La cartella di destinazione non esiste più.");
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }), () => !Busy && SelectedHistoryEntry is not null);
    }
    private void HistoryAction(Action action)
    {
        try { action(); }
        catch (Exception exception) when (Expected(exception) || exception is Win32Exception or System.Runtime.InteropServices.ExternalException)
        { Status = "Operazione non riuscita: " + exception.Message; }
    }
    private ListCollectionView CreateHistoryView()
    {
        historyFilterToday = DateTime.Today;
        ListCollectionView view = new((IList)historyEntries.ToArray());
        using (view.DeferRefresh()) { view.Filter = MatchesHistory; view.SortDescriptions.Add(new(nameof(MoveRecord.Time), ListSortDirection.Descending)); }
        return view;
    }
    private bool MatchesHistory(object item)
    {
        if (item is not MoveRecord entry) return false;
        string query = historySearch.Trim();
        if (query.Length > 0 && !entry.Source.Contains(query, StringComparison.OrdinalIgnoreCase) && !entry.Destination.Contains(query, StringComparison.OrdinalIgnoreCase) && !entry.ConfigPath.Contains(query, StringComparison.OrdinalIgnoreCase)) return false;
        if (historyState != HistoryStates[0] && HistoryDisplayConverter.StateName(entry.State) != historyState) return false;
        DateTime date = entry.Time.LocalDateTime.Date;
        if (historyPeriod == "Oggi" && date != historyFilterToday || historyPeriod == "Ieri" && date != historyFilterToday.AddDays(-1) || historyPeriod == "Ultimi 3 giorni" && (date < historyFilterToday.AddDays(-2) || date > historyFilterToday)) return false;
        return historyFolder == "Tutte le cartelle" || FileOrganizer.Core.Paths.Comparer.Equals(Path.GetDirectoryName(entry.Source), historyFolder) || FileOrganizer.Core.Paths.Comparer.Equals(Path.GetDirectoryName(entry.Destination), historyFolder);
    }
    private void FilterHistory()
    {
        historyFilterToday = DateTime.Today;
        historyView?.Refresh();
        HashSet<string> visible = HistoryView.Cast<MoveRecord>().Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        selectedHistoryIds = selectedHistoryIds.Where(visible.Contains).ToArray();
        Notify(nameof(HistoryCount)); NotifyHistorySelection();
    }
    private void HistoryEntriesChanged()
    {
        selectedHistoryIds = [];
        historyFolders = new[] { "Tutte le cartelle" }.Concat(historyEntries.SelectMany(e => new[] { Path.GetDirectoryName(e.Source), Path.GetDirectoryName(e.Destination) }).OfType<string>().Distinct(FileOrganizer.Core.Paths.Comparer).Order(FileOrganizer.Core.Paths.Comparer)).ToArray();
        if (!historyFolders.Contains(historyFolder, FileOrganizer.Core.Paths.Comparer)) { historyFolder = "Tutte le cartelle"; Notify(nameof(HistoryFolder)); }
        historyView = null;
        Notify(nameof(HistoryEntries)); Notify(nameof(HistoryFolders)); Notify(nameof(HistoryView)); Notify(nameof(HistoryCount)); NotifyHistorySelection();
    }
    private void NotifyHistorySelection()
    {
        HashSet<string> ids = selectedHistoryIds.ToHashSet(StringComparer.Ordinal);
        selectedHistoryEntry = historyEntries.FirstOrDefault(e => ids.Contains(e.Id));
        Notify(nameof(HasHistorySelection)); Notify(nameof(HistorySelectionSummary)); Notify(nameof(SelectedHistoryEntry));
        CommandManager.InvalidateRequerySuggested();
    }
    private void SetUndoOutcome(IReadOnlyList<UndoResult> results, IReadOnlyDictionary<string, MoveRecord> original)
    {
        LastUndoIssues = results.Where(r => !r.Success).Select(r => new HistoryUndoIssue(original.TryGetValue(r.Id, out MoveRecord? entry) ? Path.GetFileName(entry.Source) : r.Id, HistoryDisplayConverter.UndoReason(r.Message))).ToArray();
        UndoSummary = $"Ripristinati: {results.Count(r => r.Success)} · Saltati: {results.Count(r => !r.Success)}. Il motore resta arrestato.";
        Notify(nameof(UndoSummary)); Notify(nameof(LastUndoIssues));
    }
}
