using Npgsql;

namespace PgDumpPlane.WinForms;

internal sealed class MainForm : Form
{
    private readonly TextBox _hostTextBox = new() { Text = "localhost" };
    private readonly NumericUpDown _portInput = new() { Minimum = 1, Maximum = 65535, Value = 5432 };
    private readonly TextBox _userTextBox = new() { Text = "postgres" };
    private readonly TextBox _passwordTextBox = new() { UseSystemPasswordChar = true };
    private readonly ComboBox _sslModeComboBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _maintenanceDatabaseTextBox = new() { Text = "postgres" };

    private readonly TextBox _dumpDatabaseTextBox = new();
    private readonly TextBox _dumpPathTextBox = new();
    private readonly Button _dumpButton = new() { Text = "ダンプを作成", AutoSize = true };

    private readonly TextBox _restoreDatabaseTextBox = new();
    private readonly TextBox _restorePathTextBox = new();
    private readonly Button _restoreButton = new() { Text = "DBを削除してリストア", AutoSize = true };

    private readonly Button _cancelButton = new() { Text = "キャンセル", AutoSize = true, Enabled = false };
    private readonly ProgressBar _progressBar = new() { Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly Label _statusLabel = new() { Text = "待機中", AutoEllipsis = true, Dock = DockStyle.Fill };
    private CancellationTokenSource? _operationCancellation;

    public MainForm()
    {
        Text = "PgDumpPlane Backup & Restore";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(700, 600);
        Size = new Size(760, 650);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 9F);

        _sslModeComboBox.Items.AddRange([SslMode.Prefer, SslMode.Require, SslMode.Disable]);
        _sslModeComboBox.SelectedItem = SslMode.Prefer;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "PostgreSQL バックアップ / リストア",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold)
        };
        root.Controls.Add(title, 0, 0);
        root.Controls.Add(CreateConnectionGroup(), 0, 1);
        root.Controls.Add(CreateOperationTabs(), 0, 2);
        root.Controls.Add(CreateStatusPanel(), 0, 3);
        Controls.Add(root);

        _dumpButton.Click += async (_, _) => await DumpAsync();
        _restoreButton.Click += async (_, _) => await RestoreAsync();
        _cancelButton.Click += (_, _) => _operationCancellation?.Cancel();
    }

    private Control CreateConnectionGroup()
    {
        var group = new GroupBox
        {
            Text = "接続情報",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10)
        };
        var layout = CreateFormLayout();
        AddRow(layout, "ホスト名", _hostTextBox);
        AddRow(layout, "ポート", _portInput);
        AddRow(layout, "ユーザー名", _userTextBox);
        AddRow(layout, "パスワード", _passwordTextBox);
        AddRow(layout, "SSL Mode", _sslModeComboBox);
        AddRow(layout, "管理用DB", _maintenanceDatabaseTextBox);
        group.Controls.Add(layout);
        return group;
    }

    private Control CreateOperationTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 12) };
        tabs.TabPages.Add(CreateDumpTab());
        tabs.TabPages.Add(CreateRestoreTab());
        return tabs;
    }

    private TabPage CreateDumpTab()
    {
        var page = new TabPage("ダンプ") { Padding = new Padding(12) };
        var layout = CreateFormLayout();
        AddRow(layout, "対象DB", _dumpDatabaseTextBox);
        AddPathRow(layout, "保存先", _dumpPathTextBox, SelectDumpPath);
        var note = new Label
        {
            Text = "指定したデータベースを PgDumpPlane 形式の SQL ファイルへ保存します。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText
        };
        layout.Controls.Add(note, 1, layout.RowCount);
        layout.RowCount++;
        layout.Controls.Add(_dumpButton, 1, layout.RowCount);
        layout.RowCount++;
        page.Controls.Add(layout);
        return page;
    }

    private TabPage CreateRestoreTab()
    {
        var page = new TabPage("リストア") { Padding = new Padding(12) };
        var layout = CreateFormLayout();
        AddRow(layout, "対象DB", _restoreDatabaseTextBox);
        AddPathRow(layout, "ダンプファイル", _restorePathTextBox, SelectRestorePath);
        var warning = new Label
        {
            Text = "警告: 対象DBへの接続を切断し、DBを削除して新規作成した後にリストアします。",
            AutoSize = true,
            ForeColor = Color.Firebrick,
            Font = new Font(Font, FontStyle.Bold)
        };
        layout.Controls.Add(warning, 1, layout.RowCount);
        layout.RowCount++;
        layout.Controls.Add(_restoreButton, 1, layout.RowCount);
        layout.RowCount++;
        page.Controls.Add(layout);
        return page;
    }

    private Control CreateStatusPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.Controls.Add(_statusLabel, 0, 0);
        panel.Controls.Add(_progressBar, 1, 0);
        panel.Controls.Add(_cancelButton, 2, 0);
        return panel;
    }

    private static TableLayoutPanel CreateFormLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 0
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return layout;
    }

    private static void AddRow(TableLayoutPanel layout, string label, Control control)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 8)
        }, 0, row);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(3, 5, 3, 5);
        layout.Controls.Add(control, 1, row);
    }

    private static void AddPathRow(
        TableLayoutPanel layout,
        string label,
        TextBox textBox,
        EventHandler browseHandler)
    {
        var pathPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        textBox.Dock = DockStyle.Fill;
        var browseButton = new Button { Text = "参照...", AutoSize = true };
        browseButton.Click += browseHandler;
        pathPanel.Controls.Add(textBox, 0, 0);
        pathPanel.Controls.Add(browseButton, 1, 0);
        AddRow(layout, label, pathPanel);
    }

    private void SelectDumpPath(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "SQL ファイル (*.sql)|*.sql|すべてのファイル (*.*)|*.*",
            DefaultExt = "sql",
            AddExtension = true,
            FileName = string.IsNullOrWhiteSpace(_dumpDatabaseTextBox.Text)
                ? "database.sql"
                : $"{_dumpDatabaseTextBox.Text.Trim()}.sql"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _dumpPathTextBox.Text = dialog.FileName;
    }

    private void SelectRestorePath(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "SQL ファイル (*.sql)|*.sql|すべてのファイル (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _restorePathTextBox.Text = dialog.FileName;
    }

    private async Task DumpAsync()
    {
        if (!ValidateCommonInputs() || !RequireText(_dumpDatabaseTextBox, "対象DB") ||
            !RequireText(_dumpPathTextBox, "保存先"))
            return;

        var path = Path.GetFullPath(_dumpPathTextBox.Text.Trim());
        var database = _dumpDatabaseTextBox.Text.Trim();
        var connectionString = BuildConnectionString(database);
        await RunOperationAsync("ダンプを作成しています...", async cancellationToken =>
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            await using var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                await new PostgresPlainTextDumper().DumpAsync(
                    connectionString, output, cancellationToken: cancellationToken);
                await output.DisposeAsync();
                File.Move(temporaryPath, path, overwrite: true);
            }
            catch
            {
                await output.DisposeAsync();
                File.Delete(temporaryPath);
                throw;
            }
        }, $"ダンプを作成しました: {path}");
    }

    private async Task RestoreAsync()
    {
        if (!ValidateCommonInputs() || !RequireText(_maintenanceDatabaseTextBox, "管理用DB") ||
            !RequireText(_restoreDatabaseTextBox, "対象DB") ||
            !RequireText(_restorePathTextBox, "ダンプファイル"))
            return;

        var database = _restoreDatabaseTextBox.Text.Trim();
        var maintenanceDatabase = _maintenanceDatabaseTextBox.Text.Trim();
        var path = Path.GetFullPath(_restorePathTextBox.Text.Trim());

        if (string.Equals(database, maintenanceDatabase, StringComparison.Ordinal))
        {
            ShowError("対象DBと管理用DBには異なるデータベースを指定してください。");
            return;
        }
        if (database is "template0" or "template1")
        {
            ShowError("PostgreSQLのテンプレートDBはリストア対象に指定できません。");
            return;
        }
        if (!File.Exists(path))
        {
            ShowError("ダンプファイルが見つかりません。");
            return;
        }

        try
        {
            SetBusy(true, "ダンプファイルを検証しています...");
            _operationCancellation = new CancellationTokenSource();
            var restorer = new PostgresPlainTextRestorer();
            if (!await restorer.IsValidDumpFileAsync(path, _operationCancellation.Token))
            {
                ShowError("PgDumpPlane で作成された有効なダンプファイルではありません。");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "キャンセルしました。";
            return;
        }
        catch (Exception exception)
        {
            ShowException(exception);
            return;
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false, _statusLabel.Text);
        }

        var confirmation = MessageBox.Show(
            this,
            $"データベース「{database}」を削除し、ダンプから作り直します。\n\n" +
            "現在のデータはすべて失われます。続行しますか？",
            "DB削除の確認",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
        {
            _statusLabel.Text = "リストアを中止しました。";
            return;
        }

        var maintenanceConnectionString = BuildConnectionString(maintenanceDatabase);
        var restoreConnectionString = BuildConnectionString(database);
        await RunOperationAsync("既存DBを削除してリストアしています...", async cancellationToken =>
        {
            await RecreateDatabaseAsync(database, maintenanceConnectionString, cancellationToken);
            await new PostgresPlainTextRestorer().RestoreFileAsync(
                restoreConnectionString, path, cancellationToken: cancellationToken);
        }, $"データベース「{database}」をリストアしました。");
    }

    private async Task RecreateDatabaseAsync(
        string database,
        string maintenanceConnectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var terminateCommand = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
            "WHERE datname = @database AND pid <> pg_backend_pid();", connection))
        {
            terminateCommand.Parameters.AddWithValue("database", database);
            await terminateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var quotedDatabase = QuoteIdentifier(database);
        await using (var dropCommand = new NpgsqlCommand($"DROP DATABASE IF EXISTS {quotedDatabase};", connection))
            await dropCommand.ExecuteNonQueryAsync(cancellationToken);
        await using (var createCommand = new NpgsqlCommand($"CREATE DATABASE {quotedDatabase};", connection))
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private string BuildConnectionString(string database)
    {
        return new NpgsqlConnectionStringBuilder
        {
            Host = _hostTextBox.Text.Trim(),
            Port = decimal.ToInt32(_portInput.Value),
            Username = _userTextBox.Text.Trim(),
            Password = _passwordTextBox.Text,
            Database = database,
            SslMode = (SslMode)_sslModeComboBox.SelectedItem!,
            Timeout = 15,
            ApplicationName = "PgDumpPlane.WinForms"
        }.ConnectionString;
    }

    private async Task RunOperationAsync(
        string workingMessage,
        Func<CancellationToken, Task> operation,
        string successMessage)
    {
        _operationCancellation = new CancellationTokenSource();
        SetBusy(true, workingMessage);
        try
        {
            await operation(_operationCancellation.Token);
            _statusLabel.Text = successMessage;
            MessageBox.Show(this, successMessage, "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "キャンセルしました。";
        }
        catch (Exception exception)
        {
            ShowException(exception);
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetBusy(false, _statusLabel.Text);
        }
    }

    private bool ValidateCommonInputs() =>
        RequireText(_hostTextBox, "ホスト名") && RequireText(_userTextBox, "ユーザー名");

    private bool RequireText(TextBox textBox, string name)
    {
        if (!string.IsNullOrWhiteSpace(textBox.Text))
            return true;
        ShowError($"{name}を入力してください。");
        textBox.Focus();
        return false;
    }

    private void SetBusy(bool busy, string message)
    {
        _dumpButton.Enabled = !busy;
        _restoreButton.Enabled = !busy;
        _cancelButton.Enabled = busy;
        _progressBar.Visible = busy;
        _statusLabel.Text = message;
        UseWaitCursor = busy;
    }

    private void ShowException(Exception exception)
    {
        _statusLabel.Text = $"失敗: {exception.Message}";
        MessageBox.Show(this, exception.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void ShowError(string message)
    {
        _statusLabel.Text = message;
        MessageBox.Show(this, message, "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
