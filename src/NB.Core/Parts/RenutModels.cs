using System.Text.Json;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Project;
using NB.Core.Textures;

namespace NB.Core.Parts;

/// <summary>
/// Models for reNut custom parts (a .nbpatch's parts/*.json "model" and "attach", read by reNut's mods folder), built into a
/// bundle pair of the mod's own instead of the game's common bundle, so mods with parts never change the same file:
/// <list type="bullet">
/// <item>stream archive aid_streambundle_banjox_&lt;Bundle&gt; (Bundle/50): per model, a copy of a shipped part model's
/// streamed entry (model + shader pool) renamed aid_model_banjox_vehicleparts_&lt;Id&gt; with the OBJ imported, and its
/// collision aid_havok_banjox_vehicleparts_&lt;Id&gt;; per footprint, aid_avatarhavokdata_banjox_vehicleblock_&lt;Id&gt;.
/// Vehicles stream their parts' models and collision by id.</item>
/// <item>resident bundle aid_bundle_banjox_&lt;Bundle&gt; (Bundle/4f), only with new textures: streamed models find their
/// textures by name among the resident bundles.</item>
/// </list>
/// reNut loads every bundle file a mod with parts adds right after the game's common bundles (owner none: kept for the
/// session). Both files are new files of the workspace, so the exported .nbpatch adds them; the part files themselves
/// (&lt;workspace&gt;/renut/parts) go into it as parts/* entries (<see cref="PatchPackage"/>).
/// <para>
/// &lt;workspace&gt;/renut is laid out like a mod folder of reNut's renut-modtool (parts/, models.json and the files it
/// names), so the same files build with either tool.
/// </para>
/// </summary>
public static class RenutModels
{
    public sealed class Spec
    {
        /// <summary>The bundles' name: aid_bundle_banjox_&lt;Bundle&gt; / aid_streambundle_banjox_&lt;Bundle&gt; ([a-z0-9_]);
        /// null: <see cref="DefaultBundle"/>.</summary>
        public string? Bundle { get; set; }
        /// <summary>Models: <see cref="PartFactory.ModelSpec.Template"/> names the shipped part model to start from (e.g.
        /// gadgets_horn); <see cref="PartFactory.ModelSpec.CollisionFit"/> "footprint" fits the collision to the footprint
        /// of the same <see cref="PartFactory.ModelSpec.Id"/> in <see cref="Attach"/>; Bundles is not used.</summary>
        public List<PartFactory.ModelSpec> Models { get; set; } = new();
        /// <summary>Footprints / attach points: copies of a shipped part's (<see cref="PartFactory.AttachSpec.Template"/>).</summary>
        public List<PartFactory.AttachSpec> Attach { get; set; } = new();
    }

    const uint Common = 0x685374;
    public static string ResidentName(string bundle) => "aid_bundle_banjox_" + bundle;
    public static string StreamName(string bundle) => "aid_streambundle_banjox_" + bundle;
    /// <summary>The workspace's reNut mod folder: parts/, models.json and the model files.</summary>
    public static string ModFolder(Workspace ws) => Path.Combine(ws.Root, "renut");
    /// <summary>Where the part files of a workspace's mod live (*.json, *.lua, records): exported as parts/*.</summary>
    public static string PartsFolder(Workspace ws) => Path.Combine(ModFolder(ws), "parts");
    /// <summary>The workspace's models file (optional).</summary>
    public static string SpecPath(Workspace ws) => Path.Combine(ModFolder(ws), "models.json");

    /// <summary>The bundle name when models.json gives none: the workspace folder's name in [a-z0-9_].</summary>
    public static string DefaultBundle(Workspace ws)
    {
        var s = new string(Path.GetFileName(ws.Root.TrimEnd('\\', '/')).ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_').ToArray()).Trim('_');
        if (s.Length == 0) s = "mod";
        return "renut_" + (s.Length > 34 ? s[..34] : s);
    }

    static bool IsName(string s) => s.Length is > 0 and <= 40 && s.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    /// <summary>The models file and the files it names, newer than the models built from them (or no models built yet).</summary>
    public static bool NeedsBuild(Workspace ws, Spec spec)
    {
        var path = ws.Game.StreamPath(AssetIds.IdOf(StreamName(spec.Bundle ?? DefaultBundle(ws)))!.Value);
        if (!File.Exists(path)) return true;
        var built = File.GetLastWriteTimeUtc(path);
        var dir = ModFolder(ws);
        string P(string rel) => Path.IsPathRooted(rel) ? rel : Path.Combine(dir, rel);
        var inputs = new List<string> { SpecPath(ws) };
        foreach (var m in spec.Models)
        {
            if (!string.IsNullOrEmpty(m.Obj)) inputs.Add(P(m.Obj));
            inputs.AddRange(m.Textures.Values.Select(P));
        }
        return inputs.Any(f => File.Exists(f) && File.GetLastWriteTimeUtc(f) > built);
    }

    /// <summary>The colour textures of a shipped part model (what models.json "materials" map OBJ materials to).</summary>
    public static List<string> TemplateMaterials(Workspace ws, string template, bool turning = false)
    {
        string tpl = PartFactory.ModelAsset(template.Trim().ToLowerInvariant().Replace(PartFactory.ModelAsset(""), ""));
        uint id = AssetIds.IdOf(tpl)!.Value;
        var e = ws.LoadStream(Common).Entries.FirstOrDefault(x => x.Id == id && x.Kind == "caff") ?? throw new ArgumentException($"{tpl} is not a part model of the game");
        var c = CaffFile.Read(e.Data!);
        int sym = c.Symbols.FindIndex(s => AssetIds.DisplayName(s) == tpl) + 1;
        var ma = NB.Core.Models.ModelAsset.Parse(c, sym);
        var buffers = turning ? PartFactory.TurningBuffers(ma) : null;
        return ma.Draws.Where(x => buffers == null || buffers.Contains(x.VbRecord)).Select(x => NB.Core.Models.ObjExporter.DiffuseTexture(x)).Where(x => x != null)
            .Select(x => NB.Core.Models.ObjExporter.TextureFileStem(x!)).Distinct().ToList();
    }

    /// <summary>
    /// Checks the workspace's part files (renut/parts/*.json) the way reNut reads them: valid ids, no duplicates, a base part
    /// that exists, script and record files that are there, models and footprints that models.json or the game has. Returns
    /// the problems ("parts/x.json: ...") and the number of part files.
    /// </summary>
    public static (List<string> Problems, int Parts) CheckParts(Workspace ws, Spec? spec)
    {
        var problems = new List<string>();
        var dir = PartsFolder(ws);
        if (Directory.Exists(ModFolder(ws)))
            foreach (var f in Directory.GetFiles(ModFolder(ws), "*.json"))
                if (Path.GetFileName(f) is var n && !n.Equals("models.json", StringComparison.OrdinalIgnoreCase) && !n.Equals("mod.json", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{n}: part files go in renut\\parts; this one is in renut\\, so it isn't packed");
        bool hasModels = spec != null && (spec.Models.Count > 0 || spec.Attach.Count > 0);
        if (!Directory.Exists(dir) || Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).Length == 0)
        {
            if (hasModels) problems.Add("renut\\parts has no part files: models.json builds models, but no part uses them (a patch with only the models stops reNut at the start)");
            return (problems, 0);
        }
        var resident = ws.LoadResident(Common);
        var residentNames = resident.Symbols.Select(AssetIds.DisplayName).ToHashSet();
        HashSet<uint>? streamed = null;
        bool Streamed(string asset) => (streamed ??= ws.LoadStream(Common).Entries.Where(e => e.Kind == "caff").Select(e => e.Id).ToHashSet()).Contains(AssetIds.IdOf(asset)!.Value);
        static string Strip(string s, string prefix) { s = s.Trim().ToLowerInvariant(); return s.StartsWith(prefix) ? s[prefix.Length..] : s; }
        var ids = new HashSet<string>();
        int count = 0;
        var opt = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string rel = "parts/" + Path.GetRelativePath(dir, file).Replace('\\', '/');
            void Problem(string p) => problems.Add($"{rel}: {p}");
            JsonElement part;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), opt);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) { Problem("not a JSON object"); continue; }
                part = doc.RootElement.Clone();
            }
            catch (JsonException e) { Problem(e.Message); continue; }
            count++;
            string Str(string key) => part.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            string id = Str("id").ToLowerInvariant();
            if (!IsName(id)) Problem("\"id\" must be 1-40 characters of a-z, 0-9 and _");
            else if (!ids.Add(id)) Problem($"another part file has the id '{id}'");
            string baseName = Str("base"), objparams = Str("objparams"), script = Str("script");
            if (baseName.Length == 0 && objparams.Length == 0) Problem("needs \"base\" (a shipped part) or \"objparams\"");
            if (baseName.Length > 0)
            {
                var b = Strip(baseName, "aid_objparams_");
                if (!b.StartsWith("banjox_vehicleblock_")) b = "banjox_vehicleblock_" + b;
                if (!residentNames.Contains("aid_objparams_" + b)) Problem($"base '{baseName}' is not a part of the game");
            }
            foreach (var (key, path) in new[] { ("script", script), ("objparams", objparams) })
                if (path.Length > 0 && !File.Exists(Path.Combine(ModFolder(ws), path.Replace('/', Path.DirectorySeparatorChar))))
                    Problem($"\"{key}\": {path} isn't in renut/ (paths start with parts/)");
            string model = Strip(Strip(Str("model"), "aid_model_"), "banjox_vehicleparts_"), attach = Strip(Strip(Str("attach"), "aid_avatarhavokdata_"), "banjox_vehicleblock_");
            if (model.Length > 0 && spec?.Models.Any(m => m.Id == model) != true && !Streamed(PartFactory.ModelAsset(model)))
                Problem($"\"model\": '{model}' is neither in models.json nor a part model of the game");
            if (attach.Length > 0 && spec?.Attach.Any(a => a.Id == attach) != true && !Streamed(PartFactory.AttachPrefix + attach))
                Problem($"\"attach\": '{attach}' is neither in models.json nor a part footprint of the game");
        }
        return (problems, count);
    }

    public static List<string> Build(Workspace ws, Spec spec, string baseDir)
    {
        var log = new List<string>();
        string bundle = spec.Bundle ?? DefaultBundle(ws);
        if (!IsName(bundle)) throw new ArgumentException("\"bundle\" must be 1-40 characters of a-z, 0-9 and _");
        uint resId = AssetIds.IdOf(ResidentName(bundle))!.Value, strId = AssetIds.IdOf(StreamName(bundle))!.Value;
        if (File.Exists(ws.Original.ResidentPath(resId)) || File.Exists(ws.Original.StreamPath(strId)))
            throw new ArgumentException($"bundle name '{bundle}' gives the id of a bundle of the game ({resId & 0xFFFFFF:x6}); choose another");
        foreach (var m in spec.Models)
            if (string.IsNullOrEmpty(m.Template)) throw new ArgumentException($"model {m.Id}: \"template\" (a shipped part model, e.g. gadgets_horn) is needed");

        var src = ws.LoadStream(Common);
        var arch = new BundleArchive { Timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        // a copy of the shipped streamed asset <tpl> renamed <name>, edited, added to the archive
        string Add(string tpl, string name, Func<CaffFile, int, string>? edit)
        {
            uint tplId = AssetIds.IdOf(tpl) ?? throw new InvalidDataException("no id for " + tpl);
            var te = src.Entries.FirstOrDefault(e => e.Id == tplId && e.Kind == "caff") ?? throw new InvalidDataException($"{tpl} is not streamed by the game (Bundle/50/{Common:x6})");
            var sc = CaffFile.Read(te.Data!);
            int sym = sc.Symbols.FindIndex(s => AssetIds.DisplayName(s) == tpl) + 1;
            if (sym == 0) throw new InvalidDataException($"stream entry {tplId:X8} does not hold {tpl}");
            CaffEdit.RenameAsset(sc, sym, name);
            string note = edit?.Invoke(sc, sym) ?? "copied";
            uint id = AssetIds.IdOf(name)!.Value;
            if (arch.Entries.Any(e => e.Id == id)) throw new ArgumentException($"{name} is listed twice");
            arch.Entries.Add(new BundleEntry { Id = id, Data = sc.Write() });
            return $"{name} (0x{id:X8}): {note}";
        }

        var footprints = new Dictionary<string, AttachData>();
        foreach (var a in spec.Attach)
        {
            string tpl = PartFactory.AttachPrefix + a.Template, name = PartFactory.AttachPrefix + a.Id;
            log.Add(Add(tpl, name, (sc, sym) =>
            {
                var d = sc.PartsOf(sym).First(x => sc.SectionOf(x).Name == ".data").Data;
                var ad = AttachData.Parse(d);
                if (a.Attachable != null) { ad.SetAttachable(a.Attachable); ad.WriteFlags(d); }
                footprints[a.Id] = ad;
                return ad.ToString();
            }));
        }
        foreach (var m in spec.Models)
        {
            var meshes = PartFactory.ReadMeshes(m, baseDir);
            (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)? fit = null;
            if (m.CollisionFit == "model" && meshes != null) fit = PartFactory.Bounds(meshes);
            else if (m.CollisionFit == "footprint")
                fit = footprints.TryGetValue(m.Id, out var fp) ? PartFactory.FootprintBounds(fp)
                    : throw new ArgumentException($"model {m.Id}: collision \"footprint\" needs an attach entry with id {m.Id}");
            if (m.CollisionBox != null) fit = m.CollisionBox.ToBounds(m.Id);
            string tplModel = PartFactory.ModelAsset(m.Template!), name = PartFactory.ModelAsset(m.Id);
            log.Add(Add(tplModel, name, (sc, sym) => PartFactory.ImportModel(sc, sym, m, meshes, false)));
            log.Add(Add("aid_havok_" + tplModel["aid_model_".Length..], PartFactory.HavokAsset(m.Id), (sc, sym) => PartFactory.FitCollision(sc, sym, fit)));
        }

        // new textures: a resident bundle of their own
        var textures = spec.Models.SelectMany(m => m.Textures).GroupBy(kv => kv.Key).Select(g => g.First()).ToList();
        var resPath = ws.Game.ResidentPath(resId);
        if (textures.Count > 0)
        {
            var cc = ws.LoadResident(Common);
            var caff = NewResident(cc);
            int tsym = TextureFactory.FindTemplate(cc, XenosFormat.DXT1);
            if (tsym == 0) throw new InvalidDataException("the common bundle has no DXT1 texture to start new textures from");
            int copy = CaffEdit.CopyAsset(cc, tsym, caff, "aid_texture_banjox_renut_template");
            foreach (var (tn, png) in textures)
            {
                var (rgba, w, h) = ImageIO.Load(Path.IsPathRooted(png) ? png : Path.Combine(baseDir, png));
                TextureFactory.Create(caff, tn, rgba, w, h, w, h);
                log.Add($"texture {tn}: {w}x{h}");
            }
            CaffEdit.RemoveAsset(caff, copy);
            ws.SaveResident(resId, caff, $"reNut models {bundle}: {textures.Count} texture(s)");
            log.Add($"Bundle/4f/{resId & 0xFFFFFF:x6} ({ResidentName(bundle)}): {textures.Count} texture(s)");
        }
        else if (File.Exists(resPath))
        {
            ws.Snapshot(resPath);
            File.Delete(resPath);
            log.Add($"Bundle/4f/{resId & 0xFFFFFF:x6}: removed (no textures now)");
        }
        ws.SaveStream(strId, arch, $"reNut models {bundle}: {arch.Entries.Count} asset(s)");
        log.Add($"Bundle/50/{strId & 0xFFFFFF:x6} ({StreamName(bundle)}): {arch.Entries.Count} asset(s)");
        return log;
    }

    /// <summary>
    /// An empty resident bundle: <paramref name="template"/>'s header and sections, no assets but an empty manifest (its
    /// pointers to the entry and dependency lists kept as relocations, as in the template).
    /// </summary>
    static CaffFile NewResident(CaffFile template)
    {
        var c = new CaffFile { Version = template.Version, ExtraName = (byte[])template.ExtraName.Clone() };
        template.Header.CopyTo(c.Header, 0);
        foreach (var s in template.Sections)
            c.Sections.Add(new CaffSection { Name = s.Name, AlignLog2 = s.AlignLog2, Flags = s.Flags, Mid = (byte[])s.Mid.Clone() });
        int ms = template.Symbols.IndexOf("manifest") + 1;
        if (ms == 0) throw new InvalidDataException("the template bundle has no manifest");
        var mp = template.Parts.Select((p, i) => (p, id: i + 1)).Single(x => x.p.Symbol == ms && template.SectionOf(x.p).Name == ".data");
        var d = new byte[0x20];
        Buffer.BlockCopy(mp.p.Data, 0, d, 0, 0x20);
        BE.W32(d, 8, 0x20); BE.W32(d, 12, 0); BE.W32(d, 16, 0x20); BE.W32(d, 20, 0);
        c.Symbols.Add("manifest");
        c.Parts.Add(new CaffPart { Symbol = 1, Section = mp.p.Section, AlignLog2 = mp.p.AlignLog2, Data = d, Size = d.Length });
        foreach (var r in template.Relocs.Where(r => r.FromPart == mp.id && r.ToPart == mp.id))
            c.Relocs.Add(new CaffReloc(1, 1, (int[])r.Offsets.Clone()) { Table = r.Table });
        return c;
    }

    public static Spec LoadSpec(string path)
    {
        var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        return JsonSerializer.Deserialize<Spec>(File.ReadAllText(path), opt) ?? throw new InvalidDataException(path + ": empty");
    }
}
