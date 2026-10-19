using System.Diagnostics;
using NB.Core.Parts;

namespace NB.Studio;

/// <summary>
/// Build > reNut Parts: custom vehicle parts for reNut (the PC port), which loads them from a .nbpatch's parts/*.json
/// (variants of shipped parts, Lua-scripted new kinds of parts, own models). The workspace's renut folder is the mod:
/// renut/parts (part files and scripts, packed as parts/* by Create Distributable Patch) and renut/models.json (models
/// built into bundles of the mod's own by <see cref="RenutModels"/>). Guide: reNut's docs/modding/custom-parts.md.
/// </summary>
public sealed partial class MainForm
{
    ToolStripMenuItem RenutPartsMenu()
    {
        var m = new ToolStripMenuItem("reNut &Parts")
        {
            ToolTipText = "Custom vehicle parts for reNut: part files in the workspace's renut\\parts folder, models in renut\\models.json. " +
                          "Create Distributable Patch packs them.",
        };
        m.DropDownItems.Add("Open reNut Parts Folder", null, (_, _) => OpenRenutFolder());
        m.DropDownItems.Add("Check Part Files", null, async (_, _) => await CheckRenutParts(false));
        m.DropDownItems.Add("Build Models (renut\\models.json)", null, async (_, _) => await BuildRenutModels());
        m.DropDownItems.Add("List a Template Model's Materials…", null, async (_, _) => await ListRenutMaterials());
        return m;
    }

    void OpenRenutFolder()
    {
        if (_ws == null) return;
        Directory.CreateDirectory(RenutModels.PartsFolder(_ws));
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{RenutModels.ModFolder(_ws)}\"") { UseShellExecute = true }); }
        catch (Exception e) { Log("Could not open the folder: " + e.Message); }
    }

    /// <summary>renut/models.json, or null when there is none (or it is broken: reported).</summary>
    RenutModels.Spec? LoadRenutSpec(out bool broken)
    {
        broken = false;
        if (_ws == null || !File.Exists(RenutModels.SpecPath(_ws))) return null;
        try { return RenutModels.LoadSpec(RenutModels.SpecPath(_ws)); }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or InvalidDataException)
        {
            broken = true;
            Error("renut\\models.json can't be read", e);
            return null;
        }
    }

    /// <summary>Checks the part files; true when there are no problems.</summary>
    async Task<bool> CheckRenutParts(bool quietIfFine)
    {
        if (_ws == null || _busy) return false;
        var spec = LoadRenutSpec(out bool broken);
        if (broken) return false;
        var ws = _ws;
        _busy = true; SetProgress("Checking reNut part files…", 0.5);
        try
        {
            var (problems, parts) = await Task.Run(() => RenutModels.CheckParts(ws, spec));
            foreach (var p in problems) Log("reNut parts: " + p);
            if (problems.Count > 0)
            {
                MessageBox.Show(this, $"{problems.Count} problem(s) in the part files:\n\n" + string.Join("\n", problems.Take(12)) + (problems.Count > 12 ? "\n…" : ""),
                    "reNut Parts", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            Log(parts == 0 ? "reNut parts: no part files in renut\\parts." : $"reNut parts: {parts} part file(s), no problems.");
            if (!quietIfFine) MessageBox.Show(this, parts == 0 ? "There are no part files in renut\\parts yet." : $"{parts} part file(s), no problems.", "reNut Parts");
            return true;
        }
        catch (Exception e) { Error("Checking the part files failed", e); return false; }
        finally { _busy = false; SetProgress(null, 0); }
    }

    async Task<bool> BuildRenutModels()
    {
        if (_ws == null || _busy) return false;
        var spec = LoadRenutSpec(out bool broken);
        if (broken) return false;
        if (spec == null)
        {
            MessageBox.Show(this, $"There is no models file yet: write {RenutModels.SpecPath(_ws)} (see reNut's docs/modding/custom-parts.md).", "reNut Parts");
            return false;
        }
        var ws = _ws;
        _busy = true; SetProgress("Building reNut part models…", 0.5);
        try
        {
            var log = await Task.Run(() => RenutModels.Build(ws, spec, RenutModels.ModFolder(ws)));
            Log("reNut part models built:");
            foreach (var l in log) Log("  " + l);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException)
        {
            Error("Building the reNut part models failed", e);
            return false;
        }
        finally { _busy = false; SetProgress(null, 0); }
    }

    async Task ListRenutMaterials()
    {
        if (_ws == null || _busy) return;
        var spec = LoadRenutSpec(out _);
        var template = AskText("List a Template Model's Materials", "Shipped part model (as models.json \"template\"):",
            spec?.Models.FirstOrDefault(m => !string.IsNullOrEmpty(m.Template))?.Template ?? "gadgets_horn");
        if (string.IsNullOrWhiteSpace(template)) return;
        var ws = _ws;
        _busy = true; SetProgress("Reading the template model…", 0.5);
        try
        {
            var (stems, turning) = await Task.Run(() => (RenutModels.TemplateMaterials(ws, template), RenutModels.TemplateMaterials(ws, template, true).ToHashSet()));
            var lines = stems.Select(s => turning.Contains(s) ? s + "   (turning part: usable with \"spins\")" : s).ToList();
            Log($"Materials of {template} (models.json \"materials\" values):");
            foreach (var s in lines) Log("  " + s);
            Clipboard.SetText(string.Join(Environment.NewLine, stems));
            MessageBox.Show(this, string.Join("\n", lines) + "\n\n(also in the log, and the names are copied to the clipboard)", $"Materials of {template}");
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException) { Error("Reading the template failed", e); }
        finally { _busy = false; SetProgress(null, 0); }
    }

    /// <summary>
    /// Before Create Distributable Patch: offers to build models that are out of date and stops on part-file problems.
    /// True to go on (also when the workspace has no reNut parts).
    /// </summary>
    async Task<bool> RenutBeforePatch()
    {
        if (_ws == null || !Directory.Exists(RenutModels.ModFolder(_ws)) || !Directory.EnumerateFileSystemEntries(RenutModels.ModFolder(_ws)).Any()) return true;
        var spec = LoadRenutSpec(out bool broken);
        if (broken) return false;
        if (spec != null && (spec.Models.Count > 0 || spec.Attach.Count > 0) && RenutModels.NeedsBuild(_ws, spec))
        {
            var answer = MessageBox.Show(this, "renut\\models.json (or a file it names) changed since the models were built, or they were never built.\n\nBuild the models first?",
                "reNut Parts", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return false;
            if (answer == DialogResult.Yes && !await BuildRenutModels()) return false;
        }
        return await CheckRenutParts(true);
    }

    string? AskText(string title, string label, string initial)
    {
        using var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                                 MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(420, 110), ShowInTaskbar = false };
        var l = new Label { Text = label, Left = 12, Top = 12, Width = 396 };
        var t = new TextBox { Text = initial, Left = 12, Top = 36, Width = 396 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 252, Top = 72, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 333, Top = 72, Width = 75 };
        f.Controls.AddRange(new Control[] { l, t, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK ? t.Text.Trim() : null;
    }
}
