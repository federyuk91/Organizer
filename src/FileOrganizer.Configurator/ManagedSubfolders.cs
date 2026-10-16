using System.Windows;
using FileOrganizer.Core;

namespace FileOrganizer.Configurator;

public sealed partial class EditorViewModel
{
    private static readonly string[] ScopeFields = ["path", "recursive", "direct_paths", "recursive_paths", "excluded_paths"];
    private ConfigurationSection? cachedScopeFolder;
    private string[] cachedScopeFields = [];
    private bool cachedSubfolders;

    public bool ManageSubfolders
    {
        get
        {
            if (document is null || selectedFolder is null) return false;
            string[] values = ScopeFields.Select(key => selectedFolder.Fields[key].Text).ToArray();
            if (ReferenceEquals(cachedScopeFolder, selectedFolder) && values.SequenceEqual(cachedScopeFields)) return cachedSubfolders;
            cachedScopeFolder = selectedFolder; cachedScopeFields = values; cachedSubfolders = false;
            try
            {
                Folder folder = document.Validate().Folders[document.Folders.IndexOf(selectedFolder)];
                cachedSubfolders = new DirectoryScope(folder with { Enabled = true }).Complete(folder.Path);
            }
            catch (Exception exception) when (Expected(exception)) { cachedScopeFolder = null; }
            return cachedSubfolders;
        }
    }

    public async Task ChangeManagedSubfoldersAsync(bool include, Func<string, bool>? confirm = null)
    {
        if (!HasFolder) return;
        try
        {
            ConfigurationSection section = selectedFolder!;
            ConfigurationDocument original = document!;
            string originalText = original.Render();
            int index = document!.Folders.IndexOf(section);
            Folder folder = document.Validate().Folders[index];
            string message = include
                ? $"Includere tutti i sottopercorsi di questa cartella, anche quelli futuri?\n\n{folder.Path}\n\nLa scelta modificherà i Managed paths: sostituirà le selezioni personalizzate e le esclusioni di questo ramo con una selezione ricorsiva completa."
                : $"Includere solo i file direttamente in questa cartella?\n\n{folder.Path}\n\nLa scelta modificherà i Managed paths: rimuoverà le selezioni personalizzate dei sottopercorsi di questo ramo.";
            message += "\n\nPer un controllo più preciso usa la schermata Managed paths. Le altre cartelle e le destinazioni esterne a questo ramo rimarranno invariate.\nLa modifica resta in bozza fino a Save. Procedere?";
            bool accepted = confirm?.Invoke(message) ?? MessageBox.Show(owner, message, "Modifica Managed paths",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!accepted) return;
            // Confirmation can yield to the dispatcher; never apply to a changed selection or draft.
            if (!HasFolder || !ReferenceEquals(section, selectedFolder) || !ReferenceEquals(original, document)) return;
            if (document.Render() != originalText) throw new ConfigException("La bozza è cambiata. Ripeti l’operazione sulla configurazione corrente.");
            List<PathOverride> selections = (folder.PathOverrides.IsDefault ? [] : folder.PathOverrides)
                .Where(p => !Paths.Within(p.Path, folder.Path)).ToList();
            selections.Add(new(folder.Path, include ? DirectoryMode.Recursive : DirectoryMode.Direct));
            Dictionary<string, string> updates = new(StringComparer.Ordinal);
            foreach ((string key, DirectoryMode mode) in new[] { ("direct_paths", DirectoryMode.Direct), ("recursive_paths", DirectoryMode.Recursive), ("excluded_paths", DirectoryMode.Off) })
                updates[key] = string.Join("\n", selections.Where(p => p.Mode == mode).Select(p => p.Path));
            ConfigurationDocument candidate = new(document.Render());
            foreach ((string key, string text) in updates) candidate.Folders[index].Fields[key].Text = text;
            candidate.Validate();
            foreach ((string key, string text) in updates) section.Fields[key].Text = text;
            cachedScopeFolder = null; Notify(nameof(ManageSubfolders));
            Status = "Managed paths aggiornati nella bozza. Salva per applicare; usa Managed paths per selezioni più precise.";
            if (PathView == "Draft" && treeLayout is not null) await LoadPathsAsync();
        }
        catch (Exception exception) when (Expected(exception)) { Status = "Managed paths non modificati: " + exception.Message; }
        finally { Notify(nameof(ManageSubfolders)); }
    }
}
