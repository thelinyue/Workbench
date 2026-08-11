using System.Drawing;
using System.Windows.Forms;

namespace HephaestusWorkbench.Setup;

/// <summary>
/// 传统安装器风格的安装位置页面。
///
/// 路径是可直接编辑的完整安装目录，而不是只能通过文件夹选择器返回的父目录。
/// “浏览”按钮仅作为辅助入口，用户也可以直接输入其他盘符或自定义目录。
/// </summary>
internal sealed class InstallPathForm : Form
{
    private const string ProductDirectoryName = "HephaestusWorkbench";
    private readonly TextBox _pathTextBox;

    public InstallPathForm(string defaultDirectory)
    {
        Text = "赫工安装";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(720, 430);

        var title = new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Location = new Point(34, 24),
            Text = "选择安装位置"
        };
        var subtitle = new Label
        {
            AutoSize = true,
            Location = new Point(34, 55),
            Text = "选择赫菲斯托斯工程工作台要安装的文件夹。"
        };
        var description = new Label
        {
            AutoSize = false,
            Location = new Point(34, 105),
            Size = new Size(650, 52),
            Text = "安装程序将把赫菲斯托斯工程工作台安装在下面的文件夹中。" + Environment.NewLine +
                   "要安装到其他文件夹，请直接编辑路径，或单击“浏览(B)…”选择目录。" + Environment.NewLine +
                   "单击“安装(I)”开始安装。"
        };

        var targetGroup = new GroupBox
        {
            Location = new Point(34, 190),
            Size = new Size(650, 90),
            Text = "目标文件夹"
        };
        _pathTextBox = new TextBox
        {
            Location = new Point(20, 32),
            Size = new Size(465, 27),
            Text = NormalizeInstallDirectory(defaultDirectory),
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
        };
        var browseButton = new Button
        {
            Location = new Point(500, 30),
            Size = new Size(125, 30),
            Text = "浏览(B)…",
            UseVisualStyleBackColor = true
        };
        browseButton.Click += BrowseButton_Click;
        targetGroup.Controls.Add(_pathTextBox);
        targetGroup.Controls.Add(browseButton);

        var storageLabel = new Label
        {
            AutoSize = true,
            Location = new Point(34, 305),
            Text = "安装包约需 230 MB 磁盘空间。"
        };

        var installButton = new Button
        {
            DialogResult = DialogResult.None,
            Location = new Point(488, 360),
            Size = new Size(92, 32),
            Text = "安装(I)",
            UseVisualStyleBackColor = true
        };
        installButton.Click += InstallButton_Click;
        var cancelButton = new Button
        {
            DialogResult = DialogResult.Cancel,
            Location = new Point(592, 360),
            Size = new Size(92, 32),
            Text = "取消(C)",
            UseVisualStyleBackColor = true
        };

        Controls.Add(title);
        Controls.Add(subtitle);
        Controls.Add(description);
        Controls.Add(targetGroup);
        Controls.Add(storageLabel);
        Controls.Add(installButton);
        Controls.Add(cancelButton);
        AcceptButton = installButton;
        CancelButton = cancelButton;
        Shown += (_, _) =>
        {
            _pathTextBox.Focus();
            _pathTextBox.SelectAll();
        };
    }

    public string InstallDirectory { get; private set; } = string.Empty;

    /// <summary>将用户输入规范化为完整安装目录，并阻止直接安装到磁盘根目录。</summary>
    internal static string NormalizeInstallDirectory(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            throw new ArgumentException("请输入安装目录。", nameof(rawPath));

        var fullPath = Path.GetFullPath(rawPath.Trim());
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root)
            || string.Equals(
                Path.TrimEndingDirectorySeparator(fullPath),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("安装目录不能直接选择磁盘根目录，请输入一个子目录。", nameof(rawPath));
        }

        if (File.Exists(fullPath))
            throw new ArgumentException("安装目录已经被同名文件占用，请换一个目录。", nameof(rawPath));

        return fullPath;
    }

    private void BrowseButton_Click(object? sender, EventArgs e)
    {
        var currentPath = _pathTextBox.Text.Trim();
        var initialPath = Path.GetDirectoryName(currentPath);
        if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath))
            initialPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        using var dialog = new FolderBrowserDialog
        {
            Description = "请选择安装目录的父文件夹，安装器会使用 HephaestusWorkbench 子目录。",
            UseDescriptionForTitle = true,
            SelectedPath = initialPath,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var selectedPath = Path.GetFullPath(dialog.SelectedPath);
        _pathTextBox.Text = string.Equals(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(selectedPath)),
            ProductDirectoryName,
            StringComparison.OrdinalIgnoreCase)
            ? selectedPath
            : Path.Combine(selectedPath, ProductDirectoryName);
        _pathTextBox.SelectAll();
    }

    private void InstallButton_Click(object? sender, EventArgs e)
    {
        try
        {
            InstallDirectory = NormalizeInstallDirectory(_pathTextBox.Text);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            MessageBox.Show(this, ex.Message, "安装目录无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _pathTextBox.Focus();
            _pathTextBox.SelectAll();
        }
    }
}
