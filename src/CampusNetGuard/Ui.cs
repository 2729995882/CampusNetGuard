using System;
using System.Drawing;
using System.Windows.Forms;

namespace CampusNetGuard
{
    public sealed class StatusForm : Form
    {
        private readonly GuardContext context;
        private readonly Label statusLabel;
        private readonly Button checkButton;
        private readonly Button toggleButton;
        private readonly Button wiredButton;
        private readonly Button closeButton;

        public StatusForm(GuardContext context)
        {
            this.context = context;
            Text = "校园网助手状态";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(560, 390);
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(248, 249, 251);

            statusLabel = new Label();
            statusLabel.AutoSize = false;
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.Padding = new Padding(18, 16, 18, 16);
            statusLabel.Font = new Font("Microsoft YaHei UI", 9.5F);
            statusLabel.ForeColor = Color.FromArgb(35, 42, 54);
            statusLabel.BackColor = Color.White;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 58;
            buttons.Padding = new Padding(12, 12, 12, 8);
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;

            checkButton = new Button();
            checkButton.Text = "立即检测";
            checkButton.Width = 88;
            checkButton.Click += delegate { context.CheckNow(); };
            buttons.Controls.Add(checkButton);

            toggleButton = new Button();
            toggleButton.Text = "暂停自动重连";
            toggleButton.Width = 108;
            toggleButton.Click += delegate { context.ToggleAutoReconnect(); };
            buttons.Controls.Add(toggleButton);

            wiredButton = new Button();
            wiredButton.Text = "立即恢复有线";
            wiredButton.Width = 108;
            wiredButton.Click += delegate { context.RestoreWiredNow(); };
            buttons.Controls.Add(wiredButton);

            closeButton = new Button();
            closeButton.Text = "关闭";
            closeButton.Width = 76;
            closeButton.Click += delegate { Hide(); };
            buttons.Controls.Add(closeButton);

            Controls.Add(statusLabel);
            Controls.Add(buttons);
            UpdateFromContext();
        }

        public void UpdateFromContext()
        {
            statusLabel.Text = context.GetStatusText();
            toggleButton.Text = context.AutoReconnectEnabled ? "暂停自动重连" : "启用自动重连";
            wiredButton.Enabled = context.CanRestoreWired;
        }
    }

    public sealed class CredentialsForm : Form
    {
        private readonly GuardContext context;
        private readonly TextBox usernameBox;
        private readonly TextBox passwordBox;
        private readonly CheckBox clearPasswordBox;
        private readonly CheckBox startupBox;

        public CredentialsForm(GuardContext context)
        {
            this.context = context;
            Text = "校园网账号设置";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(430, 260);
            Font = new Font("Microsoft YaHei UI", 9F);

            Label title = new Label();
            title.Text = "账号仅保存在本机，并使用 Windows DPAPI 加密。";
            title.AutoSize = false;
            title.SetBounds(18, 14, 390, 24);
            title.ForeColor = Color.FromArgb(80, 88, 100);
            Controls.Add(title);

            Label userLabel = new Label();
            userLabel.Text = "校园网账号";
            userLabel.SetBounds(18, 52, 90, 22);
            Controls.Add(userLabel);

            usernameBox = new TextBox();
            usernameBox.SetBounds(112, 49, 292, 26);
            usernameBox.Text = context.Settings.Username;
            Controls.Add(usernameBox);

            Label pwdLabel = new Label();
            pwdLabel.Text = "密码";
            pwdLabel.SetBounds(18, 90, 90, 22);
            Controls.Add(pwdLabel);

            passwordBox = new TextBox();
            passwordBox.SetBounds(112, 87, 292, 26);
            passwordBox.UseSystemPasswordChar = true;
            passwordBox.Text = "";
            passwordBox.PasswordChar = '●';
            Controls.Add(passwordBox);

            Label hint = new Label();
            hint.Text = context.HasSavedPassword ? "已保存密码；留空表示不修改。" : "尚未保存密码，请输入一次。";
            hint.SetBounds(112, 116, 292, 22);
            hint.ForeColor = Color.FromArgb(120, 126, 136);
            Controls.Add(hint);

            clearPasswordBox = new CheckBox();
            clearPasswordBox.Text = "清除已保存密码";
            clearPasswordBox.SetBounds(112, 137, 160, 24);
            clearPasswordBox.Checked = false;
            Controls.Add(clearPasswordBox);

            startupBox = new CheckBox();
            startupBox.Text = "当前用户登录后自动启动";
            startupBox.SetBounds(112, 163, 220, 24);
            startupBox.Checked = context.StartWithWindows;
            Controls.Add(startupBox);

            Button saveButton = new Button();
            saveButton.Text = "保存";
            saveButton.SetBounds(238, 207, 80, 30);
            saveButton.Click += delegate { SaveAndClose(); };
            Controls.Add(saveButton);

            Button cancelButton = new Button();
            cancelButton.Text = "取消";
            cancelButton.SetBounds(324, 207, 80, 30);
            cancelButton.Click += delegate { Close(); };
            Controls.Add(cancelButton);

            AcceptButton = saveButton;
            CancelButton = cancelButton;
        }

        private void SaveAndClose()
        {
            string username = usernameBox.Text.Trim();
            if (string.IsNullOrEmpty(username))
            {
                MessageBox.Show(this, "请输入校园网账号。", "校园网助手",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (context.SaveCredentials(username, passwordBox.Text, clearPasswordBox.Checked, startupBox.Checked))
                Close();
        }
    }
}
