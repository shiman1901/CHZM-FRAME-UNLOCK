using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace CHZMSetup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm());
    }
}

internal sealed class SetupForm : Form
{
    private const string PayloadMarker = "CHZM_PAYLOAD_001";
    private readonly string _programs = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
    private readonly string _target;
    private readonly Label _status;
    private readonly ProgressBar _progress;
    private readonly Button _install;
    private readonly Button _open;

    public SetupForm()
    {
        _target = Path.Combine(_programs, "CHZM FRAME UNLOCK");

        Text = "CHZM FRAME UNLOCK — установка";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Width = 620;
        Height = 310;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(7, 7, 10);
        ForeColor = Color.FromArgb(240, 240, 244);
        Font = new Font("Segoe UI", 9.5f);

        var title = new Label
        {
            Text = "CHZM FRAME UNLOCK",
            Font = new Font("Segoe UI", 17f, FontStyle.Bold),
            ForeColor = Color.FromArgb(246, 246, 250),
            AutoSize = true,
            Location = new Point(28, 22)
        };

        var subtitle = new Label
        {
            Text = "Установка · ALPHA BUILD · возможны ошибки",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(214, 168, 75),
            AutoSize = true,
            Location = new Point(30, 58)
        };

        var pathLabel = new Label
        {
            Text = "Куда установится:\n" + _target,
            ForeColor = Color.FromArgb(150, 150, 160),
            AutoSize = false,
            Width = 550,
            Height = 42,
            Location = new Point(30, 92)
        };

        _status = new Label
        {
            Text = "Нажми «Установить». Понадобится ~1.1 ГБ свободного места.",
            ForeColor = Color.FromArgb(190, 190, 200),
            AutoSize = false,
            Width = 550,
            Height = 44,
            Location = new Point(30, 138)
        };

        _progress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 26,
            Visible = false,
            Width = 550,
            Height = 14,
            Location = new Point(30, 184)
        };

        _install = new Button
        {
            Text = "УСТАНОВИТЬ",
            Width = 190,
            Height = 42,
            Location = new Point(30, 212),
            BackColor = Color.FromArgb(52, 211, 153),
            ForeColor = Color.FromArgb(4, 18, 11),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };
        _install.FlatAppearance.BorderSize = 0;
        _install.Click += async (_, _) => await InstallAsync();

        _open = new Button
        {
            Text = "ОТКРЫТЬ ПАПКУ",
            Width = 170,
            Height = 42,
            Location = new Point(232, 212),
            BackColor = Color.FromArgb(18, 18, 22),
            ForeColor = Color.FromArgb(230, 230, 236),
            FlatStyle = FlatStyle.Flat,
            Enabled = false
        };
        _open.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 80);
        _open.Click += (_, _) => OpenTarget();

        Controls.AddRange(new Control[] { title, subtitle, pathLabel, _status, _progress, _install, _open });
    }

    private async Task InstallAsync()
    {
        _install.Enabled = false;
        _open.Enabled = false;
        _progress.Visible = true;

        try
        {
            if (Directory.Exists(_target))
            {
                var answer = MessageBox.Show(this,
                    "Папка уже существует:\n" + _target + "\n\nПерезаписать установку?",
                    "CHZM FRAME UNLOCK", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (answer != DialogResult.Yes)
                {
                    _status.Text = "Установка отменена.";
                    return;
                }

                _status.Text = "Удаляю старую папку…";
                await Task.Run(() => Directory.Delete(_target, true));
            }

            Directory.CreateDirectory(_programs);

            _status.Text = "Распаковываю программу и моды (≈1 ГБ). Это может занять пару минут…";

            await Task.Run(() => ExtractPayload());

            _status.Text = "Создаю ярлыки…";
            CreateShortcuts();

            _status.Text = "Готово! Ярлык «CHZM FRAME UNLOCK» создан на рабочем столе.";
            _install.Text = "ГОТОВО";
            _install.Enabled = true;
            _open.Enabled = true;
        }
        catch (Exception ex)
        {
            _status.Text = "Ошибка установки: " + ex.Message;
            MessageBox.Show(this, ex.ToString(), "Ошибка установки",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _install.Enabled = true;
        }
        finally
        {
            _progress.Visible = false;
        }
    }

    private void ExtractPayload()
    {
        string exe = Application.ExecutablePath;
        long length = new FileInfo(exe).Length;

        using var source = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
        source.Seek(-24, SeekOrigin.End);

        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        long zipLength = reader.ReadInt64();
        string marker = Encoding.ASCII.GetString(reader.ReadBytes(16));

        if (marker != PayloadMarker || zipLength <= 0 || zipLength > length - 24)
            throw new InvalidDataException("Повреждён установщик: не найден встроенный архив.");

        string tempZip = Path.Combine(Path.GetTempPath(), "chzm_frame_unlock_" + Guid.NewGuid().ToString("N") + ".zip");

        try
        {
            source.Seek(length - 24 - zipLength, SeekOrigin.Begin);

            using (var target = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[1024 * 1024];
                long left = zipLength;

                while (left > 0)
                {
                    int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (read <= 0) throw new EndOfStreamException();

                    target.Write(buffer, 0, read);
                    left -= read;
                }
            }

            ZipFile.ExtractToDirectory(tempZip, _programs, overwriteFiles: true);
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }
    }

    private void CreateShortcuts()
    {
        string exe = Path.Combine(_target, "CHZM FRAME UNLOCK.exe");
        string icon = Path.Combine(_target, "app.ico");

        CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "CHZM FRAME UNLOCK.lnk"), exe, icon);

        string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs");

        if (Directory.Exists(startMenu))
            CreateShortcut(Path.Combine(startMenu, "CHZM FRAME UNLOCK.lnk"), exe, icon);
    }

    private void CreateShortcut(string linkPath, string targetPath, string iconPath)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;

        object shell = Activator.CreateInstance(shellType)!;
        object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
            null, shell, new object[] { linkPath })!;

        Type type = shortcut.GetType();
        type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
        type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
            new object[] { _target });
        type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { iconPath });
        type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
            new object[] { "CHZM FRAME UNLOCK" });
        type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
    }

    private void OpenTarget()
    {
        try
        {
            if (!Directory.Exists(_target)) return;
            Process.Start(new ProcessStartInfo(_target) { UseShellExecute = true });
        }
        catch { }
    }
}
