using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Win32;

namespace KidHelper;

// ---------------------------------------------------------------------------
// Models
// ---------------------------------------------------------------------------
public sealed class WeaponRecord : INotifyPropertyChanged
{
	public string EditorId { get; init; } = "";
	public string Name { get; init; } = "";
	public string AnimType { get; init; } = "";
	public int Damage { get; init; }
	public float Weight { get; init; }
	public int Value { get; init; }
	public List<string> Keywords { get; init; } = new();

	public uint ObjectId { get; init; }          // FormID without the plugin byte
	public int FileIndex { get; init; }          // high byte as stored in the file
	public string DefinedIn { get; init; } = ""; // plugin that owns the record
	public bool IsOverride { get; init; }        // record edits a master's weapon
	public bool OwnerIsLight { get; init; }

	public string KeywordsText => Keywords.Count == 0 ? "— no keywords —" : string.Join(",  ", Keywords);
	public string DamageText => $"DMG {Damage}";
	public string Title => EditorId.Length > 0 ? EditorId : "(no EditorID)";

	/// <summary>KID filter for this weapon, e.g. 0x914~[Super Hero Pack].esp</summary>
	public string Filter
	{
		get {
			uint local = OwnerIsLight ? ObjectId & 0xFFF : ObjectId;
			return KidLogic.IsSkyrimEsm(DefinedIn) ? $"0x{local:X}" : $"0x{local:X}~{DefinedIn}";
		}
	}

	private string _displayId = "";
	/// <summary>xEdit-style FormID (with the load-order prefix the user typed).</summary>
	public string DisplayId { get => _displayId; set { _displayId = value; OnChanged(); } }

	private bool _isSelected;
	public bool IsSelected { get => _isSelected; set { if (_isSelected != value) { _isSelected = value; OnChanged(); } } }

	public void UpdateDisplayId(int loadOrderIndex)
	{
		if (IsOverride) DisplayId = $"{FileIndex:X2}{ObjectId:X6}";
		else if (OwnerIsLight) DisplayId = $"FE{loadOrderIndex & 0xFFF:X3}{ObjectId & 0xFFF:X3}";
		else DisplayId = $"{loadOrderIndex & 0xFF:X2}{ObjectId:X6}";
	}

	public ParsedItem ToParsedItem() =>
		new($"{Title}  \"{Name}\"  [{DisplayId}]", Filter, $"{DefinedIn}  ·  {AnimType}{(IsOverride ? "  ·  override" : "")}", true);

	public event PropertyChangedEventHandler? PropertyChanged;
	private void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new(p));
}

public sealed class PluginInfo
{
	public string FileName = "";
	public string FullPath = "";
	public bool IsMaster, IsLight, IsLocalized;
	public List<string> Masters = new();
	public List<WeaponRecord> Weapons = new();
	public List<string> MissingMasters = new();
}

// ---------------------------------------------------------------------------
// Minimal Skyrim SE plugin reader (TES4 / GRUP / records / subrecords)
// Reads only the top-level groups it needs: WEAP (+ KYWD for keyword names)
// ---------------------------------------------------------------------------
public static class EspReader
{
	private const uint FlagMaster = 0x1, FlagLocalized = 0x80, FlagLight = 0x200;
	private const uint RecDeleted = 0x20, RecCompressed = 0x40000;

	private static readonly string[] AnimNames =
		{ "Hand to Hand", "Sword", "Dagger", "War Axe", "Mace", "Greatsword", "Battleaxe / Warhammer", "Bow", "Staff", "Crossbow" };

	private static readonly Dictionary<string, Dictionary<uint, string>> KeywordCache = new(StringComparer.OrdinalIgnoreCase);
	private static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);

	public static PluginInfo Read(string path)
	{
		var info = new PluginInfo { FullPath = path, FileName = Path.GetFileName(path) };
		var ownKeywords = new Dictionary<uint, string>();
		var rawWeapons = new List<(uint fid, byte[] data)>();

		using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
		using (var br = new BinaryReader(fs)) {
			ReadHeader(br, info);
			int ownIndex = info.Masters.Count;
			ScanTopGroups(br, new[] { "WEAP", "KYWD" }, (type, flags, fid, data) => {
				if ((flags & RecDeleted) != 0) return;
				if (type == "WEAP") rawWeapons.Add((fid, data));
				else if (type == "KYWD" && (fid >> 24) >= ownIndex) {
					var edid = Subrecords(data).Where(s => s.type == "EDID").Select(s => ZString(s.data)).FirstOrDefault();
					if (edid != null) ownKeywords[fid & 0xFFFFFF] = edid;
				}
			});
		}
		KeywordCache[info.FileName] = ownKeywords;

		// master keyword names (best effort: same folder, then game Data folder)
		var dirs = new List<string> { Path.GetDirectoryName(path) ?? "" };
		var gameData = FindGameDataFolder();
		if (gameData != null) dirs.Add(gameData);
		foreach (var m in info.Masters) {
			if (KeywordCache.ContainsKey(m)) continue;
			var found = dirs.Select(d => Path.Combine(d, m)).FirstOrDefault(File.Exists);
			if (found == null) { info.MissingMasters.Add(m); continue; }
			try { KeywordCache[m] = ReadOwnKeywords(found); } catch { info.MissingMasters.Add(m); }
		}

		foreach (var (fid, data) in rawWeapons) info.Weapons.Add(ParseWeapon(fid, data, info));
		info.Weapons.Sort((a, b) => a.IsOverride != b.IsOverride ? a.IsOverride.CompareTo(b.IsOverride) : a.ObjectId.CompareTo(b.ObjectId));
		return info;
	}

	private static WeaponRecord ParseWeapon(uint fid, byte[] data, PluginInfo info)
	{
		string edid = "", name = "", anim = "?";
		int damage = 0, value = 0;
		float weight = 0;
		var kws = new List<string>();

		foreach (var (type, d) in Subrecords(data)) {
			switch (type) {
			case "EDID": edid = ZString(d); break;
			case "FULL":
				name = info.IsLocalized && d.Length == 4 ? $"(localized #{BitConverter.ToUInt32(d, 0)})" : ZString(d);
				break;
			case "KWDA":
				for (int i = 0; i + 4 <= d.Length; i += 4) kws.Add(KeywordName(BitConverter.ToUInt32(d, i), info));
				break;
			case "DNAM":
				if (d.Length > 0) anim = d[0] < AnimNames.Length ? AnimNames[d[0]] : $"Type {d[0]}";
				break;
			case "DATA":
				if (d.Length >= 10) {
					value = BitConverter.ToInt32(d, 0);
					weight = BitConverter.ToSingle(d, 4);
					damage = BitConverter.ToUInt16(d, 8);
				}
				break;
			}
		}

		int idx = (int)(fid >> 24);
		bool isOverride = idx < info.Masters.Count;
		string owner = isOverride ? info.Masters[idx] : info.FileName;
		bool ownerLight = isOverride ? owner.EndsWith(".esl", StringComparison.OrdinalIgnoreCase) : info.IsLight;

		return new WeaponRecord {
			EditorId = edid, Name = name, AnimType = anim, Damage = damage, Value = value, Weight = weight, Keywords = kws,
			ObjectId = fid & 0xFFFFFF, FileIndex = idx, DefinedIn = owner, IsOverride = isOverride, OwnerIsLight = ownerLight,
		};
	}

	private static string KeywordName(uint fid, PluginInfo info)
	{
		int idx = (int)(fid >> 24);
		string plugin = idx < info.Masters.Count ? info.Masters[idx] : info.FileName;
		uint obj = fid & 0xFFFFFF;
		if (KeywordCache.TryGetValue(plugin, out var map) && map.TryGetValue(obj, out var edid)) return edid;
		return KidLogic.IsSkyrimEsm(plugin) ? $"0x{obj:X}" : $"0x{obj:X}~{plugin}";
	}

	private static Dictionary<uint, string> ReadOwnKeywords(string path)
	{
		var map = new Dictionary<uint, string>();
		var tmp = new PluginInfo();
		using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
		using var br = new BinaryReader(fs);
		ReadHeader(br, tmp);
		int own = tmp.Masters.Count;
		ScanTopGroups(br, new[] { "KYWD" }, (type, flags, fid, data) => {
			if ((fid >> 24) < own) return;
			var edid = Subrecords(data).Where(s => s.type == "EDID").Select(s => ZString(s.data)).FirstOrDefault();
			if (edid != null) map[fid & 0xFFFFFF] = edid;
		});
		return map;
	}

	// ----------------------------------------------------------------- binary
	private static void ReadHeader(BinaryReader br, PluginInfo info)
	{
		var type = Encoding.ASCII.GetString(br.ReadBytes(4));
		if (type != "TES4") throw new InvalidDataException("Not a Skyrim plugin (missing TES4 header).");
		uint size = br.ReadUInt32();
		uint flags = br.ReadUInt32();
		br.ReadBytes(12); // formid, vc, version, unknown
		var data = br.ReadBytes((int)size);
		if ((flags & RecCompressed) != 0) data = Decompress(data);

		info.IsMaster = (flags & FlagMaster) != 0 || info.FileName.EndsWith(".esm", StringComparison.OrdinalIgnoreCase);
		info.IsLight = (flags & FlagLight) != 0 || info.FileName.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);
		info.IsLocalized = (flags & FlagLocalized) != 0;
		foreach (var (t, d) in Subrecords(data))
			if (t == "MAST") info.Masters.Add(ZString(d));
	}

	private static void ScanTopGroups(BinaryReader br, string[] wanted, Action<string, uint, uint, byte[]> onRecord)
	{
		var s = br.BaseStream;
		while (s.Position + 24 <= s.Length) {
			var tag = Encoding.ASCII.GetString(br.ReadBytes(4));
			uint size = br.ReadUInt32();
			if (tag != "GRUP") { // stray record at top level - skip
				s.Seek(16 + size, SeekOrigin.Current);
				continue;
			}
			var label = Encoding.ASCII.GetString(br.ReadBytes(4));
			br.ReadBytes(12);
			long end = s.Position - 24 + size;
			if (Array.IndexOf(wanted, label) >= 0) ReadGroupRecords(br, end, onRecord);
			s.Position = end;
		}
	}

	private static void ReadGroupRecords(BinaryReader br, long end, Action<string, uint, uint, byte[]> onRecord)
	{
		var s = br.BaseStream;
		while (s.Position + 24 <= end) {
			var type = Encoding.ASCII.GetString(br.ReadBytes(4));
			uint size = br.ReadUInt32();
			if (type == "GRUP") { s.Seek(size - 8, SeekOrigin.Current); continue; }
			uint flags = br.ReadUInt32();
			uint fid = br.ReadUInt32();
			br.ReadBytes(8);
			var data = br.ReadBytes((int)size);
			if ((flags & RecCompressed) != 0) {
				try { data = Decompress(data); } catch { continue; }
			}
			onRecord(type, flags, fid, data);
		}
	}

	private static byte[] Decompress(byte[] data)
	{
		int len = BitConverter.ToInt32(data, 0);
		var outBuf = new byte[len];
		using var z = new ZLibStream(new MemoryStream(data, 4, data.Length - 4), CompressionMode.Decompress);
		int read = 0;
		while (read < len) {
			int n = z.Read(outBuf, read, len - read);
			if (n == 0) break;
			read += n;
		}
		return outBuf;
	}

	private static IEnumerable<(string type, byte[] data)> Subrecords(byte[] d)
	{
		int p = 0;
		uint big = 0;
		while (p + 6 <= d.Length) {
			var t = Encoding.ASCII.GetString(d, p, 4);
			int sz = BitConverter.ToUInt16(d, p + 4);
			p += 6;
			if (t == "XXXX") { big = BitConverter.ToUInt32(d, p); p += sz; continue; }
			if (big != 0) { sz = (int)big; big = 0; }
			if (p + sz > d.Length) yield break;
			yield return (t, d.AsSpan(p, sz).ToArray());
			p += sz;
		}
	}

	private static string ZString(byte[] d)
	{
		int n = Array.IndexOf(d, (byte)0);
		if (n < 0) n = d.Length;
		try { return Utf8Strict.GetString(d, 0, n); } catch { return Encoding.Latin1.GetString(d, 0, n); }
	}

	public static string? FindGameDataFolder()
	{
		foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition", @"SOFTWARE\Bethesda Softworks\Skyrim Special Edition" }) {
			try {
				using var k = Registry.LocalMachine.OpenSubKey(key);
				if (k?.GetValue("installed path") is string p) {
					var data = Path.Combine(p, "Data");
					if (Directory.Exists(data)) return data;
				}
			} catch { }
		}
		return null;
	}
}
