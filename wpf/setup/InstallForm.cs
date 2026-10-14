using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Ydp.Setup
{
    /// <summary>
    /// Install dialog: lets the user pick the folder instead of accepting the
    /// one the installer assumed, and offers the shortcuts / autostart choices
    /// that were previously forced on with no way to say no.
    ///
    /// /dir &lt;path&gt; preselects a folder for scripted installs.
    /// </summary>
    internal sealed class InstallForm : Form
    {
        private readonly TextBox _path = new TextBox();
        private readonly CheckBox _desktop = new CheckBox();
        private readonly CheckBox _autoStart = new CheckBox();
        private readonly CheckBox _shortcuts = new CheckBox();
        private readonly CheckBox _engine = new CheckBox();

        public string SelectedDirectory { get { return _path.Text.Trim(); } }
        public bool CreateDesktopShortcut { get { return _desktop.Checked; } }
        public bool AddAutoStart { get { return _autoStart.Checked; } }
        public bool CreateShortcuts { get { return _shortcuts.Checked; } }
        public bool InstallEngine { get { return _engine.Checked; } }

        public InstallForm(string suggestedDir)
        {
            Text = "Установка " + Program.ProductNamePublic;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(540, 300);
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(32, 32, 32);
            ForeColor = Color.White;

            var title = new Label
            {
                Text = "Куда установить панель?",
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(20, 18),
            };
            Controls.Add(title);

            var hint = new Label
            {
                Text = "Папка создаётся автоматически. Программа работает на .NET Framework,\r\n"
                     + "который уже есть в Windows — ничего доустанавливать не нужно.",
                AutoSize = true,
                ForeColor = Color.FromArgb(185, 185, 185),
                Location = new Point(20, 50),
            };
            Controls.Add(hint);

            _path.Text = suggestedDir;
            _path.Location = new Point(20, 98);
            _path.Size = new Size(410, 24);
            _path.BackColor = Color.FromArgb(24, 24, 24);
            _path.ForeColor = Color.White;
            _path.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(_path);

            var browse = new Button
            {
                Text = "Обзор…",
                Location = new Point(440, 96),
                Size = new Size(62, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(48, 48, 48),
                ForeColor = Color.White,
            };
            browse.Click += Browse;
            Controls.Add(browse);

            _shortcuts.Text = "Создать ярлык в меню «Пуск»";
            _shortcuts.Checked = true;
            _shortcuts.ForeColor = Color.White;
            _shortcuts.AutoSize = true;
            _shortcuts.Location = new Point(20, 138);
            Controls.Add(_shortcuts);

            _desktop.Text = "Создать ярлык на рабочем столе";
            _desktop.Checked = true;
            _desktop.ForeColor = Color.White;
            _desktop.AutoSize = true;
            _desktop.Location = new Point(20, 162);
            Controls.Add(_desktop);

            _autoStart.Text = "Запускать панель при входе в Windows";
            _autoStart.Checked = true;
            _autoStart.ForeColor = Color.White;
            _autoStart.AutoSize = true;
            _autoStart.Location = new Point(20, 186);
            Controls.Add(_autoStart);

            // The zapret archive ships inside the installer, so a first run needs
            // no network and no question afterwards. Unpacked into a "Zapret"
            // folder next to the panel, which is exactly where the panel looks
            // for it.
            _engine.Text = "Установить движок zapret (папкой Zapret рядом с программой)";
            _engine.Checked = true;
            _engine.ForeColor = Color.White;
            _engine.AutoSize = true;
            _engine.Location = new Point(20, 210);
            Controls.Add(_engine);

            var install = new Button
            {
                Text = "Установить",
                Location = new Point(368, 252),
                Size = new Size(154, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 90, 190),
                ForeColor = Color.White,
                DialogResult = DialogResult.OK,
            };
            install.Click += Validate;
            Controls.Add(install);

            var cancel = new Button
            {
                Text = "Отмена",
                Location = new Point(302, 252),
                Size = new Size(60, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(56, 56, 56),
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel,
            };
            Controls.Add(cancel);

            AcceptButton = install;
            CancelButton = cancel;
        }

        private void Browse(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Выберите папку для установки панели";
                dialog.ShowNewFolderButton = true;
                if (Directory.Exists(_path.Text)) dialog.SelectedPath = _path.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) _path.Text = dialog.SelectedPath;
            }
        }

        /// <summary>Refuse an empty or clearly wrong folder before installing.</summary>
        private void Validate(object sender, EventArgs e)
        {
            string dir = SelectedDirectory;
            if (string.IsNullOrWhiteSpace(dir))
            {
                MessageBox.Show(this, "Укажите папку для установки.", Program.ProductNamePublic,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                string exe = Path.Combine(dir, Program.ProductNamePublic + ".exe");
                if (File.Exists(exe))
                {
                    var answer = MessageBox.Show(this,
                        "В этой папке уже есть " + Program.ProductNamePublic + ".\nФайлы будут заменены. Продолжить?",
                        Program.ProductNamePublic, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (answer != DialogResult.Yes)
                    {
                        DialogResult = DialogResult.None;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось проверить папку:\n" + ex.Message,
                    Program.ProductNamePublic, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}