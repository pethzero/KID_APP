using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace KidHelper;

public partial class MainWindow : Window
{
	private readonly ObservableCollection<KeywordItem> _keywords = new();
	private readonly ObservableCollection<WeaponRecord> _weapons = new();
	private readonly ObservableCollection<ParsedItem> _preview = new();
	private readonly ICollectionView _kwView, _weaponView;
	private PluginInfo? _plugin;
	private bool _ready;
	private static readonly UTF8Encoding Utf8NoBom = new(false);
	private const string AllTypes = "ทุกประเภท";
	private const string EspFilter = "Skyrim plugin (*.esp;*.esm;*.esl)|*.esp;*.esm;*.esl|All files|*.*";
	private const string IniFilter = "KID ini (*_KID.ini)|*_KID.ini|INI files (*.ini)|*.ini|All files|*.*";

	public MainWindow()
	{
		InitializeComponent();

		_kwView = CollectionViewSource.GetDefaultView(_keywords);
		_kwView.Filter = o => {
			var q = SearchBox.Text.Trim();
			var k = (KeywordItem)o;
			return q.Length == 0 || k.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || k.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
		};
		KeywordList.ItemsSource = _kwView;

		_weaponView = CollectionViewSource.GetDefaultView(_weapons);
		_weaponView.Filter = o => {
			var w = (WeaponRecord)o;
			if (WeaponTypeBox.SelectedItem is string t && t != AllTypes && w.AnimType != t) return false;
			var q = WeaponSearchBox.Text.Trim();
			return q.Length == 0
				|| w.EditorId.Contains(q, StringComparison.OrdinalIgnoreCase)
				|| w.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
				|| w.KeywordsText.Contains(q, StringComparison.OrdinalIgnoreCase)
				|| w.DisplayId.Contains(q, StringComparison.OrdinalIgnoreCase);
		};
		WeaponList.ItemsSource = _weaponView;
		PreviewList.ItemsSource = _preview;

		// Form Type: only Weapon is enabled for now
		foreach (var t in KidLogic.FormTypes)
			TypeBox.Items.Add(new ComboBoxItem { Content = t, IsEnabled = t == "Weapon" });
		TypeBox.SelectedIndex = 0;

		WeaponTypeBox.Items.Add(AllTypes);
		WeaponTypeBox.SelectedIndex = 0;

		// wiring
		PresetBox.SelectionChanged += (_, _) => { if (PresetBox.SelectedItem is KeywordPreset p) LoadPreset(p); };
		SearchBox.TextChanged += (_, _) => _kwView.Refresh();
		WeaponSearchBox.TextChanged += (_, _) => { _weaponView.Refresh(); UpdateWeaponCount(); };
		WeaponTypeBox.SelectionChanged += (_, _) => { _weaponView.Refresh(); UpdateWeaponCount(); };
		LoIndexBox.TextChanged += (_, _) => { UpdateDisplayIds(); Regenerate(); };
		foreach (var tb in new[] { KwPluginBox, DefPluginBox, TraitsBox, ChanceBox, InputBox })
			tb.TextChanged += (_, _) => Regenerate();
		foreach (var cb in new[] { ForceEslBox, MergeBox, CommentsBox, HeaderBox }) {
			cb.Checked += (_, _) => Regenerate();
			cb.Unchecked += (_, _) => Regenerate();
		}
		ModeEsp.Checked += (_, _) => SwitchMode();
		ModeManual.Checked += (_, _) => SwitchMode();
		Drop += Window_Drop;
		DragOver += (_, e) => {
			e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
			e.Handled = true;
		};

		InputBindings.Add(new KeyBinding(new RelayCommand(() => Copy_Click(this, new RoutedEventArgs())), Key.C, ModifierKeys.Control | ModifierKeys.Shift));
		InputBindings.Add(new KeyBinding(new RelayCommand(() => Save_Click(this, new RoutedEventArgs())), Key.S, ModifierKeys.Control));
		InputBindings.Add(new KeyBinding(new RelayCommand(() => OpenEsp_Click(this, new RoutedEventArgs())), Key.O, ModifierKeys.Control));

		SourceInitialized += (_, _) => ApplyDarkTitleBar();

		ReloadPresets(null);
		_ready = true;
		Regenerate();
		SetStatus("① เลือก keyword   ② ตั้งค่า   ③ เปิด ESP แล้วติ๊ก weapon   ④ Copy / Save");

		// open a plugin passed on the command line (e.g. drag onto the exe)
		var args = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
		if (args != null) Loaded += (_, _) => OpenFile(args);
	}

	// ================================================================== keyword files
	private void ReloadPresets(string? selectPath)
	{
		List<KeywordPreset> list;
		try { list = PresetStore.LoadAll(); }
		catch (Exception ex) {
			ShowToast("อ่านโฟลเดอร์ keywords ไม่ได้: " + ex.Message, error: true);
			list = Presets.All.ToList();
		}
		PresetBox.ItemsSource = list;
		selectPath ??= ReadLastPreset();
		int idx = selectPath == null ? -1
			: list.FindIndex(p => p.FilePath != null && string.Equals(p.FilePath, selectPath, StringComparison.OrdinalIgnoreCase));
		if (idx < 0) idx = list.FindIndex(p => p.Plugin.Equals("Smooth Weapon.esm", StringComparison.OrdinalIgnoreCase));
		PresetBox.SelectedIndex = Math.Max(0, idx);
	}

	// remembers the last keyword file (per user, convenience only)
	private static string LastPresetFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KIDHelper", "last_keyword_file.txt");

	private static string? ReadLastPreset()
	{
		try { return File.Exists(LastPresetFile) ? File.ReadAllText(LastPresetFile).Trim() : null; } catch { return null; }
	}

	private static void WriteLastPreset(string? path)
	{
		if (path == null) return;
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(LastPresetFile)!);
			File.WriteAllText(LastPresetFile, path);
		} catch { }
	}

	private void ReloadPresets_Click(object sender, RoutedEventArgs e)
	{
		ReloadPresets((PresetBox.SelectedItem as KeywordPreset)?.FilePath);
		ShowToast($"โหลด {PresetBox.Items.Count - 1} keyword files");
	}

	private void OpenPresetFolder_Click(object sender, RoutedEventArgs e)
	{
		Directory.CreateDirectory(PresetStore.Folder);
		Process.Start(new ProcessStartInfo("explorer.exe", $"\"{PresetStore.Folder}\"") { UseShellExecute = true });
	}

	private void LoadPreset(KeywordPreset p)
	{
		KwPluginBox.Text = p.Plugin;
		SetKeywords(KidLogic.ParseKeywordList(p.List));
		if (p.FilePath != null) {
			SetStatus($"Keyword file: {p.FilePath}");
			WriteLastPreset(p.FilePath);
		}
		if (p.List.Length == 0) EditList_Click(this, new RoutedEventArgs());
	}

	private void SetKeywords(IEnumerable<KeywordItem> items)
	{
		foreach (var k in _keywords) k.PropertyChanged -= Item_PropertyChanged;
		_keywords.Clear();
		foreach (var k in items) {
			k.PropertyChanged += Item_PropertyChanged;
			_keywords.Add(k);
		}
		Regenerate();
	}

	private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(KeywordItem.IsSelected)) {
			Regenerate();
			if (sender is WeaponRecord) UpdateWeaponCount();
		}
	}

	private void SelectAll_Click(object sender, RoutedEventArgs e) => Batch(() => { foreach (KeywordItem k in _kwView) k.IsSelected = true; });
	private void SelectNone_Click(object sender, RoutedEventArgs e) => Batch(() => { foreach (var k in _keywords) k.IsSelected = false; });

	private void EditList_Click(object sender, RoutedEventArgs e)
	{
		KwListBox.Text = string.Join("\r\n", _keywords.Select(k => $"{k.Id}\t{k.Name}".TrimEnd()));
		EditPanel.Visibility = Visibility.Visible;
		KwButtons.IsEnabled = false;
		KwListBox.Focus();
	}

	private List<KeywordItem> ApplyEditedList()
	{
		// the edit box accepts a whole keyword file too (plugin = ... lines)
		var parsed = PresetStore.Parse(KwListBox.Text, KwPluginBox.Text);
		if (parsed.Plugin.Length > 0 && !parsed.Plugin.Equals(KwPluginBox.Text, StringComparison.OrdinalIgnoreCase)
			&& Regex.IsMatch(KwListBox.Text, @"(?im)^\s*;?\s*(plugin\s*[=:]|[^\t;=|]+\.(esm|esp|esl)\s*$)"))
			KwPluginBox.Text = parsed.Plugin;

		var selected = _keywords.Where(k => k.IsSelected).Select(k => k.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var list = KidLogic.ParseKeywordList(parsed.List);
		foreach (var k in list) k.IsSelected = selected.Contains(k.Id);
		SetKeywords(list);
		EditPanel.Visibility = Visibility.Collapsed;
		KwButtons.IsEnabled = true;
		return list;
	}

	private void EditApply_Click(object sender, RoutedEventArgs e)
	{
		var list = ApplyEditedList();
		SetStatus($"ใช้ {list.Count} keywords");
	}

	private void EditSaveFile_Click(object sender, RoutedEventArgs e)
	{
		var list = ApplyEditedList();
		if (list.Count == 0) { ShowToast("list ว่าง — ไม่ได้บันทึก", error: true); return; }
		try {
			var path = PresetStore.Save(KwPluginBox.Text, list);
			ReloadPresets(path);
			ShowToast($"✓  บันทึก {Path.GetFileName(path)}");
			SetStatus($"Saved keyword file: {path}");
		} catch (Exception ex) {
			ShowToast("บันทึกไม่ได้: " + ex.Message, error: true);
		}
	}

	private void EditCancel_Click(object sender, RoutedEventArgs e)
	{
		EditPanel.Visibility = Visibility.Collapsed;
		KwButtons.IsEnabled = true;
	}

	private static readonly Regex ReKeywordLine = new(@"^\s*Keyword\s*=\s*0x[0-9A-Fa-f]+~([^|]+)\|", RegexOptions.IgnoreCase | RegexOptions.Multiline);

	private void OpenIni_Click(object sender, RoutedEventArgs e)
	{
		var dlg = new OpenFileDialog { Filter = IniFilter };
		if (dlg.ShowDialog(this) == true) LoadIniHeader(dlg.FileName);
	}

	private void LoadIniHeader(string path)
	{
		var text = File.ReadAllText(path);
		var header = string.Join("\n", KidLogic.Lines(text).Where(l => Regex.IsMatch(l, @"^\s*;\s*0x[0-9A-Fa-f]+[\s-]")));
		var list = KidLogic.ParseKeywordList(header);
		if (list.Count == 0) {
			ShowToast("ไม่พบ keyword header (;0x800 Name) ในไฟล์นี้", error: true);
			return;
		}
		var m = ReKeywordLine.Match(text);
		KwPluginBox.Text = m.Success ? m.Groups[1].Value.Trim() : "";
		SetKeywords(list);
		SetStatus($"โหลด {list.Count} keywords จาก {Path.GetFileName(path)}  —  กด ✎ แก้ list → 💾 เพื่อเก็บเป็นไฟล์ keyword");
		ShowToast($"โหลด {list.Count} keywords แล้ว");
	}

	// ================================================================== ESP
	private void OpenEsp_Click(object sender, RoutedEventArgs e)
	{
		var dlg = new OpenFileDialog { Filter = EspFilter, Title = "เปิด plugin (.esp / .esm / .esl)" };
		var gameData = EspReader.FindGameDataFolder();
		if (gameData != null) dlg.InitialDirectory = gameData;
		if (dlg.ShowDialog(this) == true) OpenFile(dlg.FileName);
	}

	private void Window_Drop(object sender, DragEventArgs e)
	{
		if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) OpenFile(files[0]);
	}

	private void OpenFile(string path)
	{
		var ext = Path.GetExtension(path).ToLowerInvariant();
		switch (ext) {
		case ".esp": case ".esm": case ".esl": LoadPlugin(path); break;
		case ".ini": LoadIniHeader(path); break;
		case ".txt":
			try {
				var dest = Path.Combine(PresetStore.Folder, Path.GetFileName(path));
				Directory.CreateDirectory(PresetStore.Folder);
				if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) File.Copy(path, dest, true);
				ReloadPresets(dest);
				ShowToast($"✓  เพิ่ม keyword file {Path.GetFileName(path)}");
			} catch (Exception ex) { ShowToast(ex.Message, error: true); }
			break;
		default: ShowToast("รองรับ .esp / .esm / .esl / _KID.ini / keyword .txt", error: true); break;
		}
	}

	private async void LoadPlugin(string path)
	{
		ModeEsp.IsChecked = true;
		EspNameText.Text = "กำลังอ่าน " + Path.GetFileName(path) + " …";
		EspInfoText.Text = "";
		PluginInfo info;
		try {
			info = await Task.Run(() => EspReader.Read(path));
		} catch (Exception ex) {
			EspNameText.Text = "อ่านไฟล์ไม่ได้";
			EspInfoText.Text = ex.Message;
			ShowToast("อ่านไฟล์ไม่ได้: " + ex.Message, error: true);
			return;
		}
		_plugin = info;

		foreach (var w in _weapons) w.PropertyChanged -= Item_PropertyChanged;
		_ready = false;
		_weapons.Clear();
		foreach (var w in info.Weapons) {
			w.PropertyChanged += Item_PropertyChanged;
			_weapons.Add(w);
		}
		UpdateDisplayIds();

		WeaponTypeBox.Items.Clear();
		WeaponTypeBox.Items.Add(AllTypes);
		foreach (var t in info.Weapons.Select(w => w.AnimType).Distinct().OrderBy(t => t)) WeaponTypeBox.Items.Add(t);
		WeaponTypeBox.SelectedIndex = 0;
		WeaponSearchBox.Text = "";
		_ready = true;

		var kind = info.IsLight ? (info.IsMaster ? "ESM + ESL flag" : "ESL / ESL-flagged") : info.IsMaster ? "ESM" : "ESP";
		int own = info.Weapons.Count(w => !w.IsOverride), ovr = info.Weapons.Count - own;
		EspNameText.Text = info.FileName;
		var sb = new StringBuilder($"{kind}  ·  {own} weapons");
		if (ovr > 0) sb.Append($"  +  {ovr} overrides");
		sb.Append(info.Masters.Count > 0 ? $"  ·  masters: {string.Join(", ", info.Masters)}" : "  ·  no masters");
		if (info.MissingMasters.Count > 0) sb.Append($"  ·  ⚠ หา master ไม่เจอ: {string.Join(", ", info.MissingMasters)} (keyword จะแสดงเป็น FormID)");
		EspInfoText.Text = sb.ToString();
		EspInfoText.ToolTip = info.FullPath + "\n" + sb;

		EspEmpty.Visibility = info.Weapons.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		if (info.Weapons.Count == 0) ShowToast("ไฟล์นี้ไม่มี Weapon record", error: true);
		else ShowToast($"✓  พบ {info.Weapons.Count} weapons ใน {info.FileName}");
		UpdateWeaponCount();
		Regenerate();
	}

	private int LoadOrderIndex() =>
		int.TryParse(LoIndexBox.Text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, null, out var v) ? v : 0;

	private void UpdateDisplayIds()
	{
		int lo = LoadOrderIndex();
		foreach (var w in _weapons) w.UpdateDisplayId(lo);
		_weaponView?.Refresh();
	}

	private void UpdateWeaponCount()
	{
		int shown = _weaponView.Cast<object>().Count();
		int sel = _weapons.Count(w => w.IsSelected);
		WeaponCountText.Text = _weapons.Count == 0 ? "" : $"เลือก {sel}  ·  แสดง {shown} / {_weapons.Count}";
		ModeEspText.Text = sel > 0 ? $"📂  จาก ESP  ({sel})" : "📂  จาก ESP";
	}

	private void WeaponSelectAll_Click(object sender, RoutedEventArgs e) => Batch(() => { foreach (WeaponRecord w in _weaponView) w.IsSelected = true; });
	private void WeaponSelectNone_Click(object sender, RoutedEventArgs e) => Batch(() => { foreach (var w in _weapons) w.IsSelected = false; });

	private void SwitchMode()
	{
		bool esp = ModeEsp.IsChecked == true;
		EspPanel.Visibility = esp ? Visibility.Visible : Visibility.Collapsed;
		InputBox.Visibility = esp ? Visibility.Collapsed : Visibility.Visible;
		if (!esp) InputBox.Focus();
	}

	private void Batch(Action a)
	{
		_ready = false;
		try { a(); } finally { _ready = true; }
		UpdateWeaponCount();
		Regenerate();
	}

	// ================================================================== generate
	private void Regenerate()
	{
		if (!_ready) return;

		var items = new List<ParsedItem>();
		items.AddRange(_weapons.Where(w => w.IsSelected).Select(w => w.ToParsedItem()));
		int manual = 0;
		foreach (var line in KidLogic.Lines(InputBox.Text)) {
			var p = KidLogic.ParseItem(line, DefPluginBox.Text, ForceEslBox.IsChecked == true);
			if (p != null) { items.Add(p); manual++; }
		}
		ModeManualText.Text = manual > 0 ? $"✎  พิมพ์เอง  ({manual})" : "✎  พิมพ์เอง";

		_preview.Clear();
		foreach (var i in items) _preview.Add(i);
		PreviewEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		int ok = items.Count(i => i.Ok), err = items.Count - ok;
		OkCountText.Text = $"{ok} OK";
		ErrCountText.Text = $"{err} ERROR";
		ErrPill.Visibility = err > 0 ? Visibility.Visible : Visibility.Collapsed;

		var selected = _keywords.Where(k => k.IsSelected).ToList();
		KwCountText.Text = $"{selected.Count} / {_keywords.Count}";

		var opt = new GenerateOptions {
			KeywordPlugin = KwPluginBox.Text,
			FormType = (TypeBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Weapon",
			Traits = TraitsBox.Text,
			Chance = ChanceBox.Text,
			Merge = MergeBox.IsChecked == true,
			Comments = CommentsBox.IsChecked == true,
			Header = HeaderBox.IsChecked == true,
		};
		var output = KidLogic.Generate(_keywords.ToList(), selected, items, opt);
		OutputBox.Text = output;

		int kwLines = KidLogic.Lines(output).Count(l => l.StartsWith("Keyword ="));
		LineCountText.Text = $"{kwLines} บรรทัด Keyword";
	}

	// ================================================================== export
	private bool HasOutput()
	{
		if (KidLogic.Lines(OutputBox.Text).Any(l => l.StartsWith("Keyword ="))) return true;
		ShowToast("ยังไม่มี output — เลือก keyword ก่อน", error: true);
		return false;
	}

	private void Copy_Click(object sender, RoutedEventArgs e)
	{
		if (!HasOutput()) return;
		try {
			Clipboard.SetText(OutputBox.Text);
			ShowToast("✓  Copied to clipboard");
		} catch (Exception ex) {
			ShowToast("Copy failed: " + ex.Message, error: true);
		}
	}

	private void Save_Click(object sender, RoutedEventArgs e)
	{
		if (!HasOutput()) return;
		var baseName = _plugin != null ? Path.GetFileNameWithoutExtension(_plugin.FileName) : "MyMod";
		foreach (var c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
		var dlg = new SaveFileDialog { Filter = IniFilter, FileName = baseName + "_KID.ini", DefaultExt = ".ini" };
		if (dlg.ShowDialog(this) != true) return;
		try {
			File.WriteAllText(dlg.FileName, OutputBox.Text, Utf8NoBom);
			AfterWrite(dlg.FileName, "Saved");
		} catch (Exception ex) { ShowToast("Save failed: " + ex.Message, error: true); }
	}

	private void Append_Click(object sender, RoutedEventArgs e)
	{
		if (!HasOutput()) return;
		var dlg = new OpenFileDialog { Filter = IniFilter, CheckFileExists = true, Title = "เลือกไฟล์ _KID.ini ที่จะต่อท้าย" };
		if (dlg.ShowDialog(this) != true) return;
		try {
			var existing = File.ReadAllText(dlg.FileName);
			var prefix = existing.Length == 0 || existing.EndsWith('\n') ? "\r\n" : "\r\n\r\n";
			File.AppendAllText(dlg.FileName, prefix + OutputBox.Text, Utf8NoBom);
			AfterWrite(dlg.FileName, "Appended");
		} catch (Exception ex) { ShowToast("Append failed: " + ex.Message, error: true); }
	}

	private void AfterWrite(string path, string verb)
	{
		var name = Path.GetFileName(path);
		var warn = name.EndsWith("_KID.ini", StringComparison.OrdinalIgnoreCase) ? "" : "   ⚠ KID อ่านเฉพาะไฟล์ที่ลงท้าย _KID.ini";
		SetStatus($"{verb}: {path}{warn}");
		ShowToast($"✓  {verb} {name}", error: warn.Length > 0);
	}

	// ================================================================== ui helpers
	private void SetStatus(string s) => StatusText.Text = s;

	private void ShowToast(string text, bool error = false)
	{
		ToastText.Text = text;
		Toast.Background = (System.Windows.Media.Brush)FindResource(error ? "ErrBrush" : "AccentBrush");
		var anim = new DoubleAnimationUsingKeyFrames();
		anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));
		anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2000))));
		anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2400))));
		Toast.BeginAnimation(OpacityProperty, anim);
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

	private void ApplyDarkTitleBar()
	{
		var hwnd = new WindowInteropHelper(this).Handle;
		int on = 1;
		DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));          // DWMWA_USE_IMMERSIVE_DARK_MODE
		int caption = 0x001C1615;                                        // #15161C as COLORREF (BGR)
		DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));      // DWMWA_CAPTION_COLOR (Win11)
	}
}

public sealed class RelayCommand : ICommand
{
	private readonly Action _run;
	public RelayCommand(Action run) => _run = run;
	public event EventHandler? CanExecuteChanged { add { } remove { } }
	public bool CanExecute(object? parameter) => true;
	public void Execute(object? parameter) => _run();
}
