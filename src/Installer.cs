// Bamboo-Win11-Installer: installiert den unveränderten Wacom-Bamboo-Treiber 5.3.5-3
// unter Windows 11. Ein neuerer Wacom-Treiber (6.x) blockiert dessen Installation still,
// deshalb wird er vorher (nach Rückfrage) entfernt. Am Ende wird das Ergebnis geprüft.
// Gebaut mit build.ps1 über den csc.exe des .NET Framework 4.x (Sprachstand C# 5).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BambooWin11
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Versteckter Selbsttest: prüft und entpackt nur, ändert nichts am System.
            if (args.Length == 2 && args[0].Equals("/selftest", StringComparison.OrdinalIgnoreCase))
                return SelfTest.Run(args[1]);
            if (args.Length == 1 && args[0].Equals("/wintab", StringComparison.OrdinalIgnoreCase))
                return WintabTest.Run();
            Application.Run(new MainForm(null));
            return 0;
        }
    }

    // Eingebettete Dateien und feste Kennwerte des Wacom-Pakets 5.3.5-3.
    static class Paket
    {
        public const string Ressource = "paket.zip";
        public const string Anleitung = "LIESMICH.txt";
        public const string RouterInf = "System32/64/Drivers/wachidrouter_con.inf";
        public const string BambooSchluessel = "Pen Tablet Driver";
        public const string Dienst = "WTabletServiceCon";
        public const string RouterDienst = "WacHidRouter";
        public static readonly Version TreiberVersion = new Version(3, 2, 1, 1);
        public static readonly Version WintabVersion = new Version(1, 0, 5, 8);

        public static ZipArchive Oeffnen()
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(Ressource);
            if (s == null) throw new InvalidOperationException("Das Treiberpaket fehlt in dieser exe.");
            return new ZipArchive(s, ZipArchiveMode.Read);
        }

        public static string AnleitungText()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(Anleitung))
            {
                if (s == null) return "";
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }
    }

    enum Stufe { Info, Ok, Warnung, Fehler }

    class Befund
    {
        public string Pruefung, Ergebnis;
        public Stufe Stufe;

        public Befund(string pruefung, string ergebnis, Stufe stufe)
        {
            Pruefung = pruefung;
            Ergebnis = ergebnis;
            Stufe = stufe;
        }
    }

    class Tablet
    {
        public string Pid, Name, Dienst;
        public bool Unterstuetzt;
    }

    class UninstallEintrag
    {
        public string Schluessel, Name, Version, Herausgeber, Befehl;
    }

    // Momentaufnahme des Systems: alles, was für die Installation zählt.
    class SystemInfo
    {
        public string WindowsText = "unbekannt";
        public bool IstWindows11, IstX64, IstArm64;
        public List<Tablet> Tablets = new List<Tablet>();
        public List<UninstallEintrag> NeuereTreiber = new List<UninstallEintrag>();
        public UninstallEintrag Bamboo;
        public List<string> Reste = new List<string>();
        public bool HvciAktiv, HvciEingeschaltet;
        public ServiceControllerStatus? DienstStatus;
        public int? WintabStufen;

        static HashSet<string> pids;
        static readonly object pidsSperre = new object();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

        public bool DienstLaeuft
        {
            get { return DienstStatus == ServiceControllerStatus.Running; }
        }

        public Tablet UnterstuetztesTablet
        {
            get { return Tablets.FirstOrDefault(t => t.Unterstuetzt); }
        }

        public bool TreiberAmTablet
        {
            get
            {
                Tablet t = UnterstuetztesTablet;
                return t != null && Paket.RouterDienst.Equals(t.Dienst, StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool WintabOk
        {
            get { return !WintabStufen.HasValue || WintabStufen.Value > 0 || WintabStufen.Value == WintabTest.NichtPruefbar; }
        }

        public bool AllesBereit
        {
            get
            {
                return Bamboo != null && NeuereTreiber.Count == 0 && Reste.Count == 0 && DienstLaeuft
                       && (UnterstuetztesTablet == null || TreiberAmTablet) && WintabOk;
            }
        }

        public static string TabletOrdner
        {
            get
            {
                string pf = Environment.GetEnvironmentVariable("ProgramW6432");
                if (string.IsNullOrEmpty(pf)) pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return Path.Combine(pf, "Tablet");
            }
        }

        // USB-Produkt-IDs, die der Bamboo-Router-Treiber laut seiner INF-Datei bedient.
        public static HashSet<string> UnterstuetztePids
        {
            get
            {
                lock (pidsSperre)
                {
                    if (pids != null) return pids;
                    pids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (ZipArchive zip = Paket.Oeffnen())
                    {
                        ZipArchiveEntry inf = zip.Entries.FirstOrDefault(e =>
                            e.FullName.Replace('\\', '/').Equals(Paket.RouterInf, StringComparison.OrdinalIgnoreCase));
                        if (inf != null)
                        {
                            string text;
                            using (var r = new StreamReader(inf.Open())) text = r.ReadToEnd();
                            foreach (Match m in Regex.Matches(text, @"USB\\VID_056A&PID_([0-9A-Fa-f]{4})"))
                                pids.Add(m.Groups[1].Value.ToUpperInvariant());
                        }
                    }
                    return pids;
                }
            }
        }

        public static SystemInfo Ermitteln()
        {
            var i = new SystemInfo();
            i.WindowsErmitteln();
            i.TabletsErmitteln();
            i.SoftwareErmitteln();
            i.HvciErmitteln();
            i.DienstErmitteln();
            if (i.Bamboo != null && i.DienstLaeuft) i.WintabStufen = WintabTest.ImEigenenProzess();
            return i;
        }

        void WindowsErmitteln()
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                string name = Wert(k, "ProductName"), version = Wert(k, "DisplayVersion");
                string build = Wert(k, "CurrentBuild"), ubr = Wert(k, "UBR");
                int b;
                int.TryParse(build, out b);
                IstWindows11 = b >= 22000;
                // Windows 11 trägt in der Registry weiterhin „Windows 10“ als Produktnamen.
                if (IstWindows11) name = name.Replace("Windows 10", "Windows 11");
                WindowsText = string.Format("{0} {1} (Build {2}{3})", name, version, build, ubr == "" ? "" : "." + ubr).Replace("  ", " ");
            }
            try
            {
                ushort prozess, nativ;
                if (IsWow64Process2(Process.GetCurrentProcess().Handle, out prozess, out nativ))
                {
                    IstX64 = nativ == 0x8664;
                    IstArm64 = nativ == 0xAA64;
                }
                else IstX64 = Environment.Is64BitOperatingSystem;
            }
            catch (EntryPointNotFoundException)
            {
                IstX64 = Environment.Is64BitOperatingSystem;
            }
            WindowsText += IstX64 ? ", 64-Bit" : IstArm64 ? ", ARM64" : ", 32-Bit";
        }

        void TabletsErmitteln()
        {
            // Nur angeschlossene Geräte; die Schnittstellen (&MI_xx) des Verbundgeräts überspringen.
            const string abfrage = "SELECT DeviceID, Name, Service FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_056A&PID_%'";
            try
            {
                using (var suche = new ManagementObjectSearcher(abfrage))
                {
                    foreach (ManagementBaseObject mo in suche.Get())
                    {
                        string id = Convert.ToString(mo["DeviceID"]);
                        if (id.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        Match m = Regex.Match(id, @"PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
                        if (!m.Success) continue;
                        var t = new Tablet
                        {
                            Pid = m.Groups[1].Value.ToUpperInvariant(),
                            Name = Convert.ToString(mo["Name"]),
                            Dienst = Convert.ToString(mo["Service"])
                        };
                        t.Unterstuetzt = UnterstuetztePids.Contains(t.Pid);
                        Tablets.Add(t);
                    }
                }
            }
            catch (ManagementException) { }
            catch (COMException) { }
        }

        void SoftwareErmitteln()
        {
            var gesehen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (UninstallEintrag e in UninstallEintraege())
            {
                if (!gesehen.Add(e.Schluessel + "|" + e.Befehl)) continue;
                if (e.Schluessel.Equals(Paket.BambooSchluessel, StringComparison.OrdinalIgnoreCase))
                {
                    Bamboo = e;
                    continue;
                }
                bool wacom = e.Herausgeber.IndexOf("Wacom", StringComparison.OrdinalIgnoreCase) >= 0;
                bool treiber = e.Befehl.IndexOf(@"\Tablet\Wacom\", StringComparison.OrdinalIgnoreCase) >= 0
                               || e.Name.Equals("Wacom Tablet", StringComparison.OrdinalIgnoreCase);
                if (wacom && treiber) NeuereTreiber.Add(e);
            }

            // Das Wacom-Setup überspringt seinen Router-Treiber, wenn in Program Files\Tablet
            // schon eine nicht ältere wachidrouter.sys liegt (Common.dat, [InstallHidRouter]).
            // Die eigenen Dateien des Bamboo-Treibers haben genau die Paketversion.
            if (Directory.Exists(TabletOrdner))
            {
                foreach (string basis in new[] { "wachidrouter", "wacomrouterfilter" })
                {
                    string sys = Path.Combine(TabletOrdner, basis + ".sys");
                    if (!File.Exists(sys) || DateiVersion(sys) == Paket.TreiberVersion) continue;
                    foreach (string f in Directory.GetFiles(TabletOrdner, basis + "*.*"))
                        if (!Reste.Contains(f)) Reste.Add(f);
                }
            }

            // Eine neuere Wintab32.dll ersetzt das Setup nicht (CopyFileIfNewer), sie spricht aber
            // nicht mit dem alten Dienst: Malprogramme hätten dann keinen Andruck.
            foreach (string ordner in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.SystemX86) })
            {
                string dll = Path.Combine(ordner, "Wintab32.dll");
                if (!File.Exists(dll)) continue;
                string firma = FileVersionInfo.GetVersionInfo(dll).CompanyName ?? "";
                if (firma.IndexOf("Wacom", StringComparison.OrdinalIgnoreCase) >= 0 && DateiVersion(dll) > Paket.WintabVersion
                    && !Reste.Contains(dll, StringComparer.OrdinalIgnoreCase))
                    Reste.Add(dll);
            }
        }

        void HvciErmitteln()
        {
            try
            {
                using (var suche = new ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard",
                                                                "SELECT SecurityServicesRunning FROM Win32_DeviceGuard"))
                {
                    foreach (ManagementBaseObject mo in suche.Get())
                    {
                        var dienste = mo["SecurityServicesRunning"] as Array;
                        if (dienste == null) continue;
                        foreach (object d in dienste)
                            if (Convert.ToUInt32(d) == 2) HvciAktiv = true;
                    }
                }
            }
            catch (ManagementException) { }
            catch (COMException) { }
            catch (UnauthorizedAccessException) { }
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity"))
            {
                HvciEingeschaltet = Wert(k, "Enabled") == "1";
            }
        }

        void DienstErmitteln()
        {
            try
            {
                using (var sc = new ServiceController(Paket.Dienst)) DienstStatus = sc.Status;
            }
            catch (InvalidOperationException)
            {
                DienstStatus = null;
            }
        }

        public List<Befund> Befunde()
        {
            var l = new List<Befund>();
            if (IstArm64) l.Add(new Befund("Windows", WindowsText + " – für ARM-Prozessoren gibt es keinen Bamboo-Treiber", Stufe.Fehler));
            else if (!IstX64) l.Add(new Befund("Windows", WindowsText + " – nur 64-Bit-Windows (x64) wird unterstützt", Stufe.Fehler));
            else l.Add(new Befund("Windows", WindowsText, Stufe.Ok));

            Tablet t = UnterstuetztesTablet;
            if (t != null) l.Add(new Befund("Tablet", TabletName(t) + " ist angeschlossen", Stufe.Ok));
            else if (Tablets.Count > 0) l.Add(new Befund("Tablet", "Wacom-Gerät 056A:" + Tablets[0].Pid + " wird vom Bamboo-Treiber nicht unterstützt", Stufe.Warnung));
            else l.Add(new Befund("Tablet", "nicht angeschlossen – kein Problem, der Treiber greift beim Einstecken", Stufe.Info));

            if (NeuereTreiber.Count > 0)
                l.Add(new Befund("Neuerer Wacom-Treiber", string.Join(", ", NeuereTreiber.Select(n => (n.Name + " " + n.Version).Trim()))
                                                         + " – blockiert das Bamboo, wird entfernt", Stufe.Warnung));
            else l.Add(new Befund("Neuerer Wacom-Treiber", "nicht installiert", Stufe.Ok));

            if (NeuereTreiber.Count > 0) l.Add(new Befund("Blockierende Dateireste", "werden nach dem Entfernen des neueren Treibers geprüft", Stufe.Info));
            else if (Reste.Count > 0)
            {
                string namen = string.Join(", ", Reste.Select(r => Path.GetFileName(r))
                                                      .Where(n => n.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                                                      .Distinct(StringComparer.OrdinalIgnoreCase));
                l.Add(new Befund("Blockierende Dateireste", Reste.Count + " Datei(en) des neueren Treibers (" + namen + ") – werden beiseitegelegt", Stufe.Warnung));
            }
            else l.Add(new Befund("Blockierende Dateireste", "keine", Stufe.Ok));

            if (Bamboo != null) l.Add(new Befund("Bamboo-Treiber", "installiert (Version " + Bamboo.Version + ")", Stufe.Ok));
            else l.Add(new Befund("Bamboo-Treiber", "noch nicht installiert", Stufe.Info));

            if (DienstLaeuft) l.Add(new Befund("Wacom-Dienst", Paket.Dienst + " läuft", Stufe.Ok));
            else if (DienstStatus.HasValue) l.Add(new Befund("Wacom-Dienst", Paket.Dienst + " ist vorhanden, läuft aber nicht", Stufe.Warnung));
            else l.Add(new Befund("Wacom-Dienst", "noch nicht vorhanden", Stufe.Info));

            if (t != null && Bamboo != null)
            {
                if (TreiberAmTablet) l.Add(new Befund("Treiber am Tablet", "Bamboo-Treiber aktiv", Stufe.Ok));
                else l.Add(new Befund("Treiber am Tablet", "noch Windows-Standardtreiber – Tablet neu einstecken oder neu starten", Stufe.Warnung));
            }

            if (WintabStufen.HasValue)
            {
                const string wintab = "Wintab (Malprogramme)";
                int w = WintabStufen.Value;
                if (w > 1) l.Add(new Befund(wintab, "verfügbar, " + w + " Druckstufen", Stufe.Ok));
                else if (w == 1) l.Add(new Befund(wintab, "verfügbar", Stufe.Ok));
                else if (w == WintabTest.NichtPruefbar) l.Add(new Befund(wintab, "konnte nicht geprüft werden", Stufe.Info));
                else if (w == WintabTest.Fehlt) l.Add(new Befund(wintab, "Wintab32.dll fehlt – kein Andruck in Malprogrammen", Stufe.Warnung));
                else l.Add(new Befund(wintab, "antwortet nicht – kein Andruck in Malprogrammen", Stufe.Warnung));
            }

            if (HvciAktiv) l.Add(new Befund("Speicherintegrität", "eingeschaltet – kann die alten Treiber von 2014 blockieren", Stufe.Warnung));
            else if (HvciEingeschaltet) l.Add(new Befund("Speicherintegrität", "wird nach dem Neustart aktiv – kann die alten Treiber blockieren", Stufe.Warnung));
            else l.Add(new Befund("Speicherintegrität", "aus", Stufe.Ok));
            return l;
        }

        public static string TabletName(Tablet t)
        {
            return t.Pid == "00D2" ? "Bamboo CTH-461 (USB-ID 056A:00D2)" : "Wacom-Tablet (USB-ID 056A:" + t.Pid + ")";
        }

        static IEnumerable<UninstallEintrag> UninstallEintraege()
        {
            foreach (RegistryView ansicht in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (RegistryKey basis = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, ansicht))
                using (RegistryKey liste = basis.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (liste == null) continue;
                    foreach (string name in liste.GetSubKeyNames())
                    {
                        using (RegistryKey k = liste.OpenSubKey(name))
                        {
                            if (k == null) continue;
                            yield return new UninstallEintrag
                            {
                                Schluessel = name,
                                Name = Wert(k, "DisplayName"),
                                Version = Wert(k, "DisplayVersion"),
                                Herausgeber = Wert(k, "Publisher"),
                                Befehl = Wert(k, "UninstallString")
                            };
                        }
                    }
                }
            }
        }

        static string Wert(RegistryKey k, string name)
        {
            if (k == null) return "";
            object v = k.GetValue(name);
            return v == null ? "" : Convert.ToString(v).Trim();
        }

        static Version DateiVersion(string pfad)
        {
            FileVersionInfo v = FileVersionInfo.GetVersionInfo(pfad);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart);
        }
    }

    class MainForm : Form
    {
        const string Erklaerung =
            "Wacom unterstützt das Bamboo CTH-461 offiziell nur bis Windows 10. Der neue Wacom-Treiber 6.x kennt es nicht mehr " +
            "(„Dieses Gerät wird nicht mehr unterstützt“). Der alte Original-Treiber 5.3.5-3 läuft unter Windows 11 aber einwandfrei. " +
            "Er wird nur blockiert, solange ein neuerer Wacom-Treiber installiert ist.\n\n" +
            "Beim Klick auf „Installieren“:\n" +
            "1.  wird ein neuerer Wacom-Treiber nach Rückfrage deinstalliert,\n" +
            "2.  werden blockierende Dateireste beiseitegelegt (nicht gelöscht),\n" +
            "3.  startet das unveränderte Wacom-Setup 5.3.5-3 (dort Sprache und Lizenz bestätigen),\n" +
            "4.  wird geprüft, ob Treiber und Wacom-Dienst laufen.";

        static readonly string LogDatei = Path.Combine(Path.GetTempPath(), "Bamboo-Win11-Installer.log");

        readonly float skala;
        readonly SystemInfo vorab;
        readonly ListView liste;
        readonly TextBox protokoll;
        readonly ProgressBar fortschritt;
        readonly Label status;
        readonly Button btnInstallieren, btnNeustart, btnAnleitung, btnSchliessen;
        bool laeuft;

        // vorab != null: Befunde sofort anzeigen und keine eigene Prüfung starten (Selbsttest).
        public MainForm(SystemInfo vorab)
        {
            this.vorab = vorab;
            Text = "Bamboo-Treiber für Windows 11";
            Font = new Font("Segoe UI", 9.75f);
            BackColor = SystemColors.Window;
            AutoScaleMode = AutoScaleMode.None;
            using (Graphics g = CreateGraphics()) skala = g.DpiX / 96f;

            var tabelle = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(S(18), S(12), S(18), S(12))
            };
            tabelle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var titel = new Label
            {
                Text = "Bamboo-Treiber für Windows 11",
                Font = new Font("Segoe UI Semibold", 15f),
                AutoSize = true,
                Margin = new Padding(0)
            };
            var untertitel = new Label
            {
                Text = "Wacom Bamboo Pen & Touch CTH-461  ·  unveränderter Original-Treiber 5.3.5-3 von Wacom",
                UseMnemonic = false,
                AutoSize = true,
                ForeColor = Color.FromArgb(96, 96, 96),
                Margin = new Padding(0, S(2), 0, S(10))
            };
            var erklaerung = new TextBox
            {
                Text = Erklaerung.Replace("\n", "\r\n"),
                Multiline = true,
                ReadOnly = true,
                TabStop = false,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Window,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, S(6))
            };
            erklaerung.Resize += delegate { BildlaufBeiBedarf(erklaerung); };

            liste = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                ShowItemToolTips = true,
                MultiSelect = false,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, S(6))
            };
            liste.Columns.Add("Status", S(80));
            liste.Columns.Add("Prüfung", S(185));
            liste.Columns.Add("Ergebnis", S(500));
            liste.Resize += delegate { SpaltenAnpassen(); };

            protokoll = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(247, 247, 247),
                Dock = DockStyle.Fill,
                Margin = new Padding(0)
            };
            fortschritt = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Dock = DockStyle.Fill,
                Visible = false,
                Margin = new Padding(0, S(4), 0, 0)
            };

            btnAnleitung = Knopf("Anleitung");
            btnInstallieren = Knopf("Installieren");
            btnNeustart = Knopf("Neu starten");
            btnSchliessen = Knopf("Schließen");
            btnInstallieren.Enabled = false;
            btnNeustart.Visible = false;
            btnAnleitung.Margin = new Padding(0);

            status = new Label
            {
                Text = "Prüfe System …",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                UseMnemonic = false,
                Margin = new Padding(S(12), 0, S(8), 0)
            };
            var knoepfe = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(0)
            };
            knoepfe.Controls.AddRange(new Control[] { btnNeustart, btnInstallieren, btnSchliessen });

            var fuss = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                AutoSize = true,
                Margin = new Padding(0, S(10), 0, 0)
            };
            fuss.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fuss.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fuss.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fuss.Controls.Add(btnAnleitung, 0, 0);
            fuss.Controls.Add(status, 1, 0);
            fuss.Controls.Add(knoepfe, 2, 0);

            Zeile(tabelle, titel, SizeType.AutoSize, 0);
            Zeile(tabelle, untertitel, SizeType.AutoSize, 0);
            Zeile(tabelle, erklaerung, SizeType.Absolute, S(182));
            Zeile(tabelle, Ueberschrift("Systemprüfung"), SizeType.AutoSize, 0);
            Zeile(tabelle, liste, SizeType.Absolute, S(214));
            Zeile(tabelle, Ueberschrift("Ablauf"), SizeType.AutoSize, 0);
            Zeile(tabelle, protokoll, SizeType.Percent, 100);
            Zeile(tabelle, fortschritt, SizeType.Absolute, S(10));
            Zeile(tabelle, fuss, SizeType.AutoSize, 0);
            Controls.Add(tabelle);

            // Fenster passend zur Bildschirmhöhe; das Protokoll nimmt den Rest.
            Rectangle flaeche = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(Math.Min(S(880), flaeche.Width - S(40)), Math.Min(S(720), flaeche.Height - S(60)));
            MinimumSize = new Size(S(720), S(560));
            StartPosition = FormStartPosition.CenterScreen;
            AcceptButton = btnInstallieren;

            btnInstallieren.Click += delegate { Starten(); };
            btnSchliessen.Click += delegate { Close(); };
            btnAnleitung.Click += delegate { AnleitungZeigen(); };
            btnNeustart.Click += delegate { NeuStarten(); };
            FormClosing += (s, e) =>
            {
                if (!laeuft) return;
                e.Cancel = true;
                MessageBox.Show(this, "Bitte warten, bis die Installation abgeschlossen ist.", Text,
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            Shown += delegate
            {
                SpaltenAnpassen();
                if (vorab == null) Pruefen();
            };

            if (vorab != null)
            {
                BefundeZeigen(vorab);
                NachPruefung(vorab);
            }
        }

        int S(int px)
        {
            return (int)Math.Round(px * skala);
        }

        Button Knopf(string text)
        {
            return new Button
            {
                Text = text,
                AutoSize = false,
                Size = new Size(S(124), S(32)),
                Margin = new Padding(S(8), 0, 0, 0),
                UseVisualStyleBackColor = true
            };
        }

        Label Ueberschrift(string text)
        {
            return new Label
            {
                Text = text,
                Font = new Font("Segoe UI Semibold", 10.5f),
                AutoSize = true,
                Margin = new Padding(0, S(6), 0, S(4))
            };
        }

        static void Zeile(TableLayoutPanel t, Control c, SizeType art, float hoehe)
        {
            t.RowStyles.Add(art == SizeType.AutoSize ? new RowStyle(SizeType.AutoSize) : new RowStyle(art, hoehe));
            t.Controls.Add(c, 0, t.RowCount);
            t.RowCount++;
        }

        // Bildlaufleiste nur, wenn der Text bei der aktuellen Breite nicht ganz hineinpasst.
        static void BildlaufBeiBedarf(TextBox t)
        {
            int breite = t.Width - SystemInformation.VerticalScrollBarWidth - 8;
            if (breite <= 0) return;
            Size noetig = TextRenderer.MeasureText(t.Text, t.Font, new Size(breite, int.MaxValue),
                                                   TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            ScrollBars soll = noetig.Height > t.ClientSize.Height ? ScrollBars.Vertical : ScrollBars.None;
            if (t.ScrollBars != soll) t.ScrollBars = soll;
        }

        void SpaltenAnpassen()
        {
            if (liste.Columns.Count < 3) return;
            int rest = liste.ClientSize.Width - liste.Columns[0].Width - liste.Columns[1].Width;
            liste.Columns[2].Width = Math.Max(S(200), rest);
        }

        void ImUi(Action a)
        {
            if (InvokeRequired) Invoke(a);
            else a();
        }

        public void Log(string text)
        {
            string zeile = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
            try
            {
                File.AppendAllText(LogDatei, DateTime.Now.ToString("yyyy-MM-dd ") + zeile + "\r\n", Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            ImUi(() =>
            {
                if (protokoll.TextLength > 0) protokoll.AppendText("\r\n");
                protokoll.AppendText(zeile);
            });
        }

        void Status(string text)
        {
            ImUi(() => status.Text = text);
        }

        DialogResult Frage(string text, MessageBoxIcon symbol, bool jaVorgewaehlt)
        {
            DialogResult antwort = DialogResult.None;
            ImUi(() =>
            {
                antwort = MessageBox.Show(this, text, Text, MessageBoxButtons.YesNo, symbol,
                                          jaVorgewaehlt ? MessageBoxDefaultButton.Button1 : MessageBoxDefaultButton.Button2);
            });
            return antwort;
        }

        void Meldung(string text, MessageBoxIcon symbol)
        {
            ImUi(() => MessageBox.Show(this, text, Text, MessageBoxButtons.OK, symbol));
        }

        void BefundeZeigen(SystemInfo info)
        {
            ImUi(() =>
            {
                liste.BeginUpdate();
                liste.Items.Clear();
                foreach (Befund b in info.Befunde())
                {
                    var eintrag = new ListViewItem(new[] { StufeText(b.Stufe), b.Pruefung, b.Ergebnis })
                    {
                        UseItemStyleForSubItems = false,
                        ToolTipText = b.Ergebnis
                    };
                    eintrag.SubItems[0].ForeColor = StufeFarbe(b.Stufe);
                    eintrag.SubItems[0].Font = new Font(liste.Font, FontStyle.Bold);
                    liste.Items.Add(eintrag);
                }
                liste.EndUpdate();
                SpaltenAnpassen();
            });
        }

        static string StufeText(Stufe s)
        {
            switch (s)
            {
                case Stufe.Ok: return "OK";
                case Stufe.Warnung: return "Achtung";
                case Stufe.Fehler: return "Fehler";
                default: return "Info";
            }
        }

        static Color StufeFarbe(Stufe s)
        {
            switch (s)
            {
                case Stufe.Ok: return Color.FromArgb(0, 128, 60);
                case Stufe.Warnung: return Color.FromArgb(190, 100, 0);
                case Stufe.Fehler: return Color.FromArgb(200, 0, 0);
                default: return Color.FromArgb(0, 90, 170);
            }
        }

        void Pruefen()
        {
            Log("Systemprüfung …");
            var t = new Thread(() =>
            {
                try
                {
                    SystemInfo info = SystemInfo.Ermitteln();
                    BefundeZeigen(info);
                    ImUi(() => NachPruefung(info));
                    Log("Systemprüfung abgeschlossen.");
                }
                catch (Exception ex)
                {
                    Log("Fehler bei der Systemprüfung: " + ex.Message);
                    Status("Systemprüfung fehlgeschlagen – Details im Ablauf.");
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        void NachPruefung(SystemInfo info)
        {
            btnInstallieren.Enabled = info.IstX64;
            if (!info.IstX64)
                status.Text = "Auf diesem System ist die Installation nicht möglich.";
            else if (info.AllesBereit)
            {
                status.Text = "Alles ist bereits eingerichtet.";
                btnInstallieren.Text = "Neu installieren";
            }
            else if (info.Bamboo != null && info.NeuereTreiber.Count == 0)
            {
                status.Text = "Reparatur empfohlen – siehe Systemprüfung.";
                btnInstallieren.Text = "Reparieren";
            }
            else status.Text = "Bereit – klicke auf „Installieren“.";
            if (btnInstallieren.Enabled) ActiveControl = btnInstallieren;
        }

        void Starten()
        {
            laeuft = true;
            btnInstallieren.Enabled = btnSchliessen.Enabled = btnNeustart.Visible = false;
            fortschritt.Visible = true;
            var t = new Thread(Installieren);
            t.IsBackground = true;
            t.Start();
        }

        void Installieren()
        {
            bool fertig = false;
            try
            {
                fertig = Ablauf();
            }
            catch (Exception ex)
            {
                Log("FEHLER: " + ex);
                Status("Fehler – Details im Ablauf.");
                Meldung("Es ist ein Fehler aufgetreten:\n\n" + ex.Message + "\n\nDetails stehen im Protokoll:\n" + LogDatei, MessageBoxIcon.Error);
            }
            ImUi(() =>
            {
                laeuft = false;
                fortschritt.Visible = false;
                btnSchliessen.Enabled = true;
                btnInstallieren.Enabled = !fertig;
                btnNeustart.Visible = fertig;
            });
        }

        // Liefert true, wenn der Bamboo-Treiber am Ende installiert ist.
        bool Ablauf()
        {
            Status("Prüfe System …");
            SystemInfo info = SystemInfo.Ermitteln();
            BefundeZeigen(info);

            if (info.Bamboo != null && info.NeuereTreiber.Count == 0)
            {
                string frage = info.Reste.Count > 0
                    ? "Der Bamboo-Treiber ist installiert, aber im System liegen noch " + info.Reste.Count + " Datei(en) eines neueren " +
                      "Wacom-Treibers. Sie verhindern zum Beispiel den Andruck in Malprogrammen.\n\n" +
                      "Jetzt reparieren? Die Dateien werden beiseitegelegt und das Wacom-Setup 5.3.5-3 erneut ausgeführt."
                    : "Der Bamboo-Treiber 5.3.5-3 ist bereits installiert.\n\nWacom-Setup trotzdem erneut ausführen (z. B. zur Reparatur)?";
                if (Frage(frage, MessageBoxIcon.Question, true) != DialogResult.Yes)
                {
                    Log("Abgebrochen – nichts verändert.");
                    Status("Abgebrochen.");
                    return false;
                }
            }

            // 1. Neueren Wacom-Treiber mit dessen eigenem Deinstallationsprogramm entfernen.
            if (info.NeuereTreiber.Count > 0)
            {
                foreach (UninstallEintrag e in info.NeuereTreiber)
                {
                    string frage = string.Format(
                        "Installiert ist „{0}“ (Version {1}).\n\n" +
                        "Dieser neuere Wacom-Treiber unterstützt das Bamboo nicht und verhindert die Installation des Bamboo-Treibers. " +
                        "Er wird jetzt mit seinem eigenen Deinstallationsprogramm entfernt.\n\n" +
                        "Wichtig: Wenn du an diesem PC zusätzlich ein neueres Wacom-Gerät nutzt (z. B. Intuos, One, Cintiq), brauchst du " +
                        "diesen Treiber – dann bitte „Nein“ wählen. Beide Treiber können nicht gleichzeitig installiert sein.\n\n" +
                        "Falls das Wacom-Programm am Ende einen Neustart anbietet: „Später“ wählen.\n\nJetzt entfernen?",
                        e.Name, e.Version);
                    if (Frage(frage, MessageBoxIcon.Warning, false) != DialogResult.Yes)
                    {
                        Log("Abgebrochen – nichts verändert.");
                        Status("Abgebrochen.");
                        return false;
                    }
                    Status("Entferne " + e.Name + " …");
                    Log("Entferne " + e.Name + " " + e.Version + " …");
                    string datei, argumente;
                    BefehlZerlegen(e.Befehl, out datei, out argumente);
                    int code = Ausfuehren(datei, argumente, null);
                    Log("Deinstallation beendet (Rückgabewert " + code + ").");
                }
                for (int i = 0; i < 30; i++)
                {
                    info = SystemInfo.Ermitteln();
                    if (info.NeuereTreiber.Count == 0) break;
                    Thread.Sleep(1000);
                }
                BefundeZeigen(info);
                if (info.NeuereTreiber.Count > 0)
                {
                    Log("Der neuere Wacom-Treiber ist noch installiert.");
                    Status("Abgebrochen – neuerer Wacom-Treiber noch vorhanden.");
                    Meldung("Der neuere Wacom-Treiber ist noch installiert.\n\nBitte über Einstellungen › Apps entfernen, " +
                            "den PC neu starten und dieses Programm danach erneut ausführen.", MessageBoxIcon.Warning);
                    return false;
                }
            }

            // 2. Dateireste des neueren Treibers, die das Wacom-Setup nicht ersetzt.
            if (info.Reste.Count > 0)
            {
                string ablage = Path.Combine(SystemInfo.TabletOrdner, "_beiseitegelegt_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Log("Lege blockierende Dateireste beiseite nach " + ablage + " …");
                try
                {
                    foreach (string f in info.Reste)
                    {
                        // Unterordner nach Herkunft (Tablet, System32, SysWOW64), damit nichts kollidiert.
                        string unterordner = Path.Combine(ablage, Path.GetFileName(Path.GetDirectoryName(f)));
                        Directory.CreateDirectory(unterordner);
                        File.Move(f, Path.Combine(unterordner, Path.GetFileName(f)));
                        Log("  " + f);
                    }
                }
                catch (Exception ex)
                {
                    Log("Konnte nicht verschoben werden: " + ex.Message);
                    Status("Abgebrochen – bitte neu starten und erneut ausführen.");
                    Meldung("Einige alte Treiberdateien sind noch in Benutzung.\n\nBitte den PC neu starten und dieses Programm danach erneut ausführen.",
                            MessageBoxIcon.Warning);
                    return false;
                }
            }

            // 3. Original-Paket entpacken und das Wacom-Setup unverändert ausführen.
            string ordner = Path.Combine(Path.GetTempPath(), "Bamboo-5.3.5-3_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Status("Entpacke Treiberpaket …");
            Log("Entpacke Original-Treiberpaket 5.3.5-3 …");
            using (ZipArchive zip = Paket.Oeffnen()) zip.ExtractToDirectory(ordner);

            Status("Wacom-Setup läuft – bitte Sprache und Lizenz bestätigen …");
            Log("Starte Wacom-Setup 5.3.5-3 – bitte Sprache und Lizenz bestätigen.");
            int rueckgabe = Ausfuehren(Path.Combine(ordner, "setup.exe"), "", ordner);
            Log("Wacom-Setup beendet (Rückgabewert " + rueckgabe + ").");

            // 4. Ergebnis prüfen; der Dienst braucht nach dem Setup oft ein paar Sekunden.
            Status("Prüfe Ergebnis …");
            for (int i = 0; i < 20; i++)
            {
                info = SystemInfo.Ermitteln();
                if (info.Bamboo == null || (info.DienstLaeuft && (info.UnterstuetztesTablet == null || info.TreiberAmTablet) && info.WintabOk)) break;
                Thread.Sleep(1000);
            }
            BefundeZeigen(info);
            try
            {
                Directory.Delete(ordner, true);
            }
            catch (Exception ex)
            {
                Log("Hinweis: Temporärer Ordner bleibt liegen (" + ex.Message + ").");
            }

            if (info.Bamboo == null)
            {
                Log("Der Bamboo-Treiber ist nicht installiert – wurde das Wacom-Setup abgebrochen?");
                Status("Bamboo-Treiber nicht installiert.");
                Meldung("Der Bamboo-Treiber ist nicht installiert. Wurde das Wacom-Setup abgebrochen?\n\nEinfach erneut auf „Installieren“ klicken.",
                        MessageBoxIcon.Warning);
                return false;
            }

            string ergebnis;
            if (!info.DienstLaeuft)
                ergebnis = "Der Bamboo-Treiber ist installiert, der Wacom-Dienst läuft aber noch nicht. Bitte den PC neu starten.";
            else if (!info.WintabOk)
                ergebnis = "Der Bamboo-Treiber ist installiert, aber die Wintab-Schnittstelle für Malprogramme antwortet nicht. " +
                           "Bitte den PC neu starten und dieses Programm danach noch einmal öffnen.";
            else if (info.UnterstuetztesTablet == null)
                ergebnis = "Der Bamboo-Treiber ist installiert. Tablet einstecken – fertig. Ein Neustart wird trotzdem empfohlen.";
            else if (!info.TreiberAmTablet)
                ergebnis = "Der Bamboo-Treiber ist installiert. Bitte das Tablet aus- und wieder einstecken oder den PC neu starten.";
            else
                ergebnis = "Fertig – das Bamboo ist einsatzbereit. Ein Neustart wird empfohlen, damit Windows alte Reste aufräumt.\n\n" +
                           "Einstellungen findest du im Startmenü unter Wacom › „Wacom Voreinstellungen“.";
            Log(ergebnis.Replace("\n\n", " "));
            Status(info.AllesBereit ? "Fertig – das Bamboo ist einsatzbereit." : "Installiert – bitte neu starten.");
            Meldung(ergebnis, info.AllesBereit ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return true;
        }

        static void BefehlZerlegen(string befehl, out string datei, out string argumente)
        {
            befehl = befehl.Trim();
            if (befehl.StartsWith("\""))
            {
                int ende = befehl.IndexOf('"', 1);
                datei = befehl.Substring(1, ende - 1);
                argumente = befehl.Substring(ende + 1).Trim();
                return;
            }
            int exe = befehl.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            datei = exe < 0 ? befehl : befehl.Substring(0, exe + 4);
            argumente = exe < 0 ? "" : befehl.Substring(exe + 4).Trim();
        }

        static int Ausfuehren(string datei, string argumente, string arbeitsordner)
        {
            var info = new ProcessStartInfo(datei, argumente) { UseShellExecute = true };
            if (arbeitsordner != null) info.WorkingDirectory = arbeitsordner;
            using (Process p = Process.Start(info))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        void AnleitungZeigen()
        {
            try
            {
                string pfad = Path.Combine(Path.GetTempPath(), "Bamboo-Win11-LIESMICH.txt");
                File.WriteAllText(pfad, Paket.AnleitungText(), Encoding.UTF8);
                Process.Start("notepad.exe", "\"" + pfad + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Die Anleitung konnte nicht geöffnet werden: " + ex.Message, Text,
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void NeuStarten()
        {
            if (MessageBox.Show(this, "Jetzt neu starten?\n\nBitte vorher offene Dokumente speichern.", Text,
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true });
        }
    }

    // Fragt das Tablet über Wintab32.dll ab, so wie es Malprogramme tun.
    static class WintabTest
    {
        public const int KeineAntwort = 0, Fehlt = -2, NichtPruefbar = -3;

        [DllImport("Wintab32.dll", CharSet = CharSet.Unicode)]
        static extern uint WTInfoW(uint kategorie, uint index, IntPtr ausgabe);

        const uint WTI_DEVICES = 100, DVC_NPRESSURE = 15;

        // Rückgabe: Anzahl der Druckstufen, 1 = Wintab da, aber ohne Druckangabe, sonst einer der Fehlerwerte.
        public static int Run()
        {
            try
            {
                if (WTInfoW(0, 0, IntPtr.Zero) == 0) return KeineAntwort;
                uint groesse = WTInfoW(WTI_DEVICES, DVC_NPRESSURE, IntPtr.Zero);
                if (groesse < 8) return 1;
                IntPtr achse = Marshal.AllocHGlobal((int)groesse);
                try
                {
                    WTInfoW(WTI_DEVICES, DVC_NPRESSURE, achse);
                    int maximum = Marshal.ReadInt32(achse, 4);
                    return maximum > 0 ? maximum + 1 : 1;
                }
                finally
                {
                    Marshal.FreeHGlobal(achse);
                }
            }
            catch (DllNotFoundException) { return Fehlt; }
            catch (EntryPointNotFoundException) { return Fehlt; }
            catch (BadImageFormatException) { return Fehlt; }
        }

        // Eigener Prozess, damit immer die gerade installierte Wintab32.dll geladen wird.
        public static int ImEigenenProzess()
        {
            try
            {
                var info = new ProcessStartInfo(Application.ExecutablePath, "/wintab") { UseShellExecute = false, CreateNoWindow = true };
                using (Process p = Process.Start(info))
                {
                    if (p.WaitForExit(15000)) return p.ExitCode;
                    p.Kill();
                    return NichtPruefbar;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return NichtPruefbar;
            }
        }
    }

    // Prüft Erkennung, Paket und Fensteraufbau, ohne etwas am System zu ändern.
    static class SelfTest
    {
        public static int Run(string ziel)
        {
            Directory.CreateDirectory(ziel);
            var sb = new StringBuilder();
            SystemInfo info = SystemInfo.Ermitteln();
            sb.AppendLine("Windows: " + info.WindowsText + " | Win11=" + info.IstWindows11 + " x64=" + info.IstX64 + " ARM64=" + info.IstArm64);
            sb.AppendLine("Unterstützte PIDs: " + SystemInfo.UnterstuetztePids.Count + " (00D2 dabei: " + SystemInfo.UnterstuetztePids.Contains("00D2") + ")");
            foreach (Tablet t in info.Tablets)
                sb.AppendLine("Tablet: " + t.Pid + " | " + t.Name + " | Dienst=" + t.Dienst + " | unterstützt=" + t.Unterstuetzt);
            foreach (UninstallEintrag n in info.NeuereTreiber)
                sb.AppendLine("Neuerer Treiber: " + n.Name + " " + n.Version + " | " + n.Befehl);
            sb.AppendLine("Bamboo: " + (info.Bamboo == null ? "-" : info.Bamboo.Name + " " + info.Bamboo.Version + " | " + info.Bamboo.Befehl));
            foreach (string r in info.Reste) sb.AppendLine("Rest: " + r);
            sb.AppendLine("HVCI aktiv/eingeschaltet: " + info.HvciAktiv + "/" + info.HvciEingeschaltet);
            sb.AppendLine("Dienst: " + (info.DienstStatus.HasValue ? info.DienstStatus.Value.ToString() : "-") +
                          " | Wintab=" + (info.WintabStufen.HasValue ? info.WintabStufen.Value.ToString() : "-") + " | AllesBereit=" + info.AllesBereit);
            foreach (Befund b in info.Befunde()) sb.AppendLine("[" + b.Stufe + "] " + b.Pruefung + ": " + b.Ergebnis);

            string entpackt = Path.Combine(ziel, "entpackt");
            if (Directory.Exists(entpackt)) Directory.Delete(entpackt, true);
            using (ZipArchive zip = Paket.Oeffnen()) zip.ExtractToDirectory(entpackt);
            string[] dateien = Directory.GetFiles(entpackt, "*", SearchOption.AllDirectories);
            sb.AppendLine("Entpackt: " + dateien.Length + " Dateien, " + dateien.Sum(f => new FileInfo(f).Length) + " Bytes, setup.exe: " +
                          File.Exists(Path.Combine(entpackt, "setup.exe")));
            sb.AppendLine("Anleitung: " + Paket.AnleitungText().Length + " Zeichen");

            using (var f = new MainForm(info))
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-5000, -5000);
                f.ShowInTaskbar = false;
                f.Show();
                Application.DoEvents();
                using (var bild = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bild, new Rectangle(Point.Empty, f.Size));
                    bild.Save(Path.Combine(ziel, "fenster.png"), ImageFormat.Png);
                }
                f.Close();
            }
            File.WriteAllText(Path.Combine(ziel, "selftest.txt"), sb.ToString(), Encoding.UTF8);
            return 0;
        }
    }
}
