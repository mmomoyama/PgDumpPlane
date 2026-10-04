using Npgsql;

namespace PgDumpPlane.WinForms;

/// <summary>
/// <para>ダンプ作成と対象DBの再作成を伴う復元を行うWindows Formsサンプルです。</para>
/// <para>Provides the Windows Forms sample for dumping and restoring with database recreation.</para>
/// </summary>
internal sealed class MainForm : Form
{
    // 接続情報はダンプと復元で共有します。
    // Connection settings are shared by dump and restore operations.
    private readonly TextBox _hostTextBox = new() { Text = "localhost" };
    private readonly NumericUpDown _portInput = new() { Minimum = 1, Maximum = 65535, Value = 5432 };
    private readonly TextBox _userTextBox = new() { Text = "postgres" };
    private readonly TextBox _passwordTextBox = new() { UseSystemPasswordChar = true };
    private readonly ComboBox _sslModeComboBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _maintenanceDatabaseTextBox = new() { Text = "postgres" };

    // ダンプの入力欄と、PgDumpOptionsへ渡す選択項目です。
    // Dump inputs and selections mapped to PgDumpOptions.
    private readonly TextBox _dumpDatabaseTextBox = new();
    private readonly TextBox _dumpPathTextBox = new();
    private readonly TextBox _includeSchemasTextBox = new() { PlaceholderText = "例: public, sales（空欄はすべて）" };
    private readonly TextBox _excludeSchemasTextBox = new() { PlaceholderText = "例: audit, work" };
    private readonly CheckBox _includeSchemaCheckBox = new() { Text = "スキーマ定義を含める", Checked = true, AutoSize = true };
    private readonly CheckBox _includeDataCheckBox = new() { Text = "テーブルデータとシーケンス値を含める", Checked = true, AutoSize = true };
    private readonly ComboBox _dataFormatComboBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _includeUnloggedDataCheckBox = new() { Text = "UNLOGGEDテーブルのデータを含める", Checked = true, AutoSize = true };
    private readonly CheckBox _serializableDeferrableCheckBox = new() { Text = "SERIALIZABLE / DEFERRABLEスナップショットを使用", AutoSize = true };
    private readonly CheckBox _usePsqlRestrictCheckBox = new() { Text = "psqlの \\restrictガードを出力", Checked = true, AutoSize = true };
    private readonly CheckBox _includeOwnershipCheckBox = new() { Text = "所有者を含める", Checked = true, AutoSize = true };
    private readonly CheckBox _includePrivilegesCheckBox = new() { Text = "権限を含める", Checked = true, AutoSize = true };
    private readonly CheckBox _includeRoleSettingsCheckBox = new() { Text = "ロール設定を含める（クラスタ全体に影響）", AutoSize = true };
    private readonly Button _dumpButton = new() { Text = "ダンプを作成", AutoSize = true };

    // 復元先と、PgRestoreOptionsへ渡す選択項目です。
    // Restore target and selections mapped to PgRestoreOptions.
    private readonly TextBox _restoreDatabaseTextBox = new();
    private readonly TextBox _restorePathTextBox = new();
    private readonly CheckBox _useTransactionCheckBox = new() { Text = "単一トランザクションでリストア", Checked = true, AutoSize = true };
    private readonly NumericUpDown _commandTimeoutInput = new() { Minimum = 0, Maximum = 86400, Value = 0 };
    private readonly Button _restoreButton = new() { Text = "DBを削除してリストア", AutoSize = true };

    private readonly Button _cancelButton = new() { Text = "キャンセル", AutoSize = true, Enabled = false };
    private readonly ProgressBar _progressBar = new() { Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly Label _statusLabel = new() { Text = "待機中", AutoEllipsis = true, Dock = DockStyle.Fill };
    // 同時に一つの操作を実行し、キャンセルボタンからその操作へ通知します。
    // Run one operation at a time and route cancellation from the button to that operation.
    private CancellationTokenSource? _operationCancellation;

    /// <summary>
    /// <para>接続・ダンプ・復元の画面を構築し、操作イベントを登録します。</para>
    /// <para>Builds the connection, dump, and restore UI and registers operation handlers.</para>
    /// </summary>
    public MainForm()
    {
        Text = "PgDumpPlane Backup & Restore";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 680);
        Size = new Size(840, 800);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 9F);

        _sslModeComboBox.Items.AddRange([SslMode.Prefer, SslMode.Require, SslMode.Disable]);
        _sslModeComboBox.SelectedItem = SslMode.Prefer;
        _dataFormatComboBox.Items.AddRange([PgDumpDataFormat.Copy, PgDumpDataFormat.Inserts]);
        _dataFormatComboBox.SelectedItem = PgDumpDataFormat.Copy;

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

    /// <summary>
    /// <para>接続情報を入力するグループを構築します。</para>
    /// <para>Builds the group for entering connection settings.</para>
    /// </summary>
    /// <returns>構築されたUIコントロール。 The constructed UI control.</returns>
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

    /// <summary>
    /// <para>ダンプと復元のタブをまとめます。</para>
    /// <para>Creates the tab control containing dump and restore pages.</para>
    /// </summary>
    /// <returns>構築されたUIコントロール。 The constructed UI control.</returns>
    private Control CreateOperationTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 12) };
        tabs.TabPages.Add(CreateDumpTab());
        tabs.TabPages.Add(CreateRestoreTab());
        return tabs;
    }

    /// <summary>
    /// <para>保存先とダンプ設定を選ぶタブを構築します。</para>
    /// <para>Builds the tab for choosing the output path and dump settings.</para>
    /// </summary>
    /// <returns>構築されたUIコントロール。 The constructed UI control.</returns>
    private TabPage CreateDumpTab()
    {
        var page = new TabPage("ダンプ") { Padding = new Padding(12), AutoScroll = true };
        var layout = CreateFormLayout();
        AddRow(layout, "対象DB", _dumpDatabaseTextBox);
        AddPathRow(layout, "保存先", _dumpPathTextBox, SelectDumpPath);
        AddRow(layout, "オプション", CreateDumpOptionsPanel());
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

    /// <summary>
    /// <para>入力ファイルと復元設定、およびDB削除の警告を表示するタブを構築します。</para>
    /// <para>Builds the restore tab with input settings and the database-deletion warning.</para>
    /// </summary>
    /// <returns>構築されたUIコントロール。 The constructed UI control.</returns>
    private TabPage CreateRestoreTab()
    {
        var page = new TabPage("リストア") { Padding = new Padding(12), AutoScroll = true };
        var layout = CreateFormLayout();
        AddRow(layout, "対象DB", _restoreDatabaseTextBox);
        AddPathRow(layout, "ダンプファイル", _restorePathTextBox, SelectRestorePath);
        AddRow(layout, "オプション", CreateRestoreOptionsPanel());
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

    /// <summary>
    /// <para>ダンプのスキーマフィルターと各出力オプションを配置します。</para>
    /// <para>Lays out schema filters and dump output options.</para>
    /// </summary>
    /// <returns>構築されたUIコントロール。 The constructed UI control.</returns>
    private Control CreateDumpOptionsPanel()
    {
        var layout = CreateFormLayout();
        layout.ColumnStyles[0].Width = 150;
        AddRow(layout, "対象スキーマ", _includeSchemasTextBox);
        AddRow(layout, "除外スキーマ", _excludeSchemasTextBox);
        AddRow(layout, "データ形式", _dataFormatComboBox);
        AddRow(layout, "出力内容", CreateVerticalOptionsPanel(
            _includeSchemaCheckBox,
            _includeDataCheckBox,
            _includeUnloggedDataCheckBox,
            _includeOwnershipCheckBox,
            _includePrivilegesCheckBox,
            _includeRoleSettingsCheckBox,
            _serializableDeferrableCheckBox,
            _usePsqlRestrictCheckBox));
        return layout;
    }

    /// <summary>
    /// <para>復元トランザクションとSQLタイムアウトの設定を配置します。</para>
    /// <para>Lays out restore transaction and SQL timeout settings.</para>
    /// </summary>
    /// <returns>構築されたUIコントロール。 The constructed UI control.</returns>
    private Control CreateRestoreOptionsPanel()
    {
        var layout = CreateFormLayout();
        layout.ColumnStyles[0].Width = 150;
        AddRow(layout, "実行方法", _useTransactionCheckBox);

        var timeoutPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Dock = DockStyle.Fill
        };
        _commandTimeoutInput.Width = 100;
        timeoutPanel.Controls.Add(_commandTimeoutInput);
        timeoutPanel.Controls.Add(new Label
        {
            Text = "秒（0は無制限）",
            AutoSize = true,
            Margin = new Padding(3, 8, 3, 3)
        });
        AddRow(layout, "タイムアウト", timeoutPanel);
        return layout;
    }

    /// <summary>
    /// <para>複数のコントロールを折り返さず縦方向に並べます。</para>
    /// <para>Arranges controls vertically without wrapping.</para>
    /// </summary>
    /// <param name="controls">配置するコントロール一覧。 Controls to arrange.</param>
    /// <returns>構築されたUIコントロール。 The constructed UI control.</returns>
    private static Control CreateVerticalOptionsPanel(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Dock = DockStyle.Fill
        };
        panel.Controls.AddRange(controls);
        return panel;
    }

    /// <summary>
    /// <para>進捗表示、状態メッセージ、キャンセル操作を配置します。</para>
    /// <para>Lays out progress, status, and cancellation controls.</para>
    /// </summary>
    /// <returns>構築されたUIコントロール。 The constructed UI control.</returns>
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

    /// <summary>
    /// <para>ラベル列と入力列からなる共通レイアウトを作成します。</para>
    /// <para>Creates the shared two-column label-and-input layout.</para>
    /// </summary>
    /// <returns>構築されたUIコントロール。 The constructed UI control.</returns>
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

    /// <summary>
    /// <para>共通レイアウトにラベルと入力コントロールの行を追加します。</para>
    /// <para>Adds a labeled input row to the shared layout.</para>
    /// </summary>
    /// <param name="layout">行を追加するレイアウト。 Layout receiving the row.</param>
    /// <param name="label">入力欄のラベル。 Label for the input field.</param>
    /// <param name="control">配置する入力コントロール。 Input control to arrange.</param>
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

    /// <summary>
    /// <para>パス入力と参照ボタンを一つの行として追加します。</para>
    /// <para>Adds a path input and browse button as one row.</para>
    /// </summary>
    /// <param name="layout">行を追加するレイアウト。 Layout receiving the row.</param>
    /// <param name="label">入力欄のラベル。 Label for the input field.</param>
    /// <param name="textBox">入力または検証するテキストボックス。 Text box to populate or validate.</param>
    /// <param name="browseHandler">参照ボタンのクリック処理。 Browse button click handler.</param>
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

    /// <summary>
    /// <para>保存ダイアログを表示し、選択されたダンプ出力先を反映します。</para>
    /// <para>Shows the save dialog and applies the selected dump output path.</para>
    /// </summary>
    /// <param name="sender">イベントを発生させたオブジェクト。 Object raising the event.</param>
    /// <param name="e">イベントの引数。 Event arguments.</param>
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

    /// <summary>
    /// <para>ファイル選択ダイアログで復元元のダンプを選びます。</para>
    /// <para>Shows the file dialog for selecting the dump to restore.</para>
    /// </summary>
    /// <param name="sender">イベントを発生させたオブジェクト。 Object raising the event.</param>
    /// <param name="e">イベントの引数。 Event arguments.</param>
    private void SelectRestorePath(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "SQL / ダンプファイル (*.sql;*.dmp)|*.sql;*.dmp|すべてのファイル (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _restorePathTextBox.Text = dialog.FileName;
    }

    /// <summary>
    /// <para>DBをダンプし、一時ファイルへの書き込み成功後に保存先を置き換えます。</para>
    /// <para>Dumps the database and replaces the output file only after a temporary file is written successfully.</para>
    /// </summary>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private async Task DumpAsync()
    {
        if (!ValidateCommonInputs() || !RequireText(_dumpDatabaseTextBox, "対象DB") ||
            !RequireText(_dumpPathTextBox, "保存先"))
            return;

        if (!_includeSchemaCheckBox.Checked && !_includeDataCheckBox.Checked)
        {
            ShowError("「スキーマ定義を含める」または「テーブルデータとシーケンス値を含める」を選択してください。");
            return;
        }

        var path = Path.GetFullPath(_dumpPathTextBox.Text.Trim());
        var database = _dumpDatabaseTextBox.Text.Trim();
        var connectionString = BuildConnectionString(database);
        var options = CreateDumpOptions();
        await RunOperationAsync("ダンプを作成しています...", async cancellationToken =>
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // 既存のバックアップを途中出力で上書きしないよう、同じディレクトリの一時ファイルへ保存します。
            // Write to a temporary file in the same directory so partial output does not overwrite an existing backup.
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            await using var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                await new PostgresPlainTextDumper().DumpAsync(
                    connectionString, output, options, cancellationToken);
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

    /// <summary>
    /// <para>ヘッダー確認とユーザーの確認後、対象DBを再作成してダンプを復元します。</para>
    /// <para>Validates the header, obtains user confirmation, recreates the target database, and restores the dump.</para>
    /// </summary>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private async Task RestoreAsync()
    {
        if (!ValidateCommonInputs() || !RequireText(_maintenanceDatabaseTextBox, "管理用DB") ||
            !RequireText(_restoreDatabaseTextBox, "対象DB") ||
            !RequireText(_restorePathTextBox, "ダンプファイル"))
            return;

        var database = _restoreDatabaseTextBox.Text.Trim();
        var maintenanceDatabase = _maintenanceDatabaseTextBox.Text.Trim();
        var path = Path.GetFullPath(_restorePathTextBox.Text.Trim());

        // 削除するDBへ接続したままDROP DATABASEはできないため、別の管理用DBが必要です。
        // DROP DATABASE cannot run while connected to its target; use a separate maintenance database.
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
                ShowError("PgDumpPlane または pg_dump で作成された有効なプレーンSQLダンプではありません。");
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

        // 削除・再作成は不可逆なので、形式確認後かつ破壊的操作の前に明示的な確認を求めます。
        // Dropping and recreating the database is irreversible; confirm after header validation and before deletion.
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
        var options = new PgRestoreOptions
        {
            UseTransaction = _useTransactionCheckBox.Checked,
            CommandTimeout = decimal.ToInt32(_commandTimeoutInput.Value)
        };
        await RunOperationAsync("既存DBを削除してリストアしています...", async cancellationToken =>
        {
            await RecreateDatabaseAsync(database, maintenanceConnectionString, cancellationToken);
            await new PostgresPlainTextRestorer().RestoreFileAsync(
                restoreConnectionString, path, options, cancellationToken);
        }, $"データベース「{database}」をリストアしました。");
    }

    /// <summary>
    /// <para>画面の選択状態をライブラリのダンプオプションに変換します。</para>
    /// <para>Converts UI selections into library dump options.</para>
    /// </summary>
    /// <returns>画面の入力を反映したダンプ設定。 Dump options reflecting UI input.</returns>
    private PgDumpOptions CreateDumpOptions()
    {
        var options = new PgDumpOptions
        {
            IncludeSchema = _includeSchemaCheckBox.Checked,
            IncludeData = _includeDataCheckBox.Checked,
            DataFormat = (PgDumpDataFormat)_dataFormatComboBox.SelectedItem!,
            IncludeUnloggedTableData = _includeUnloggedDataCheckBox.Checked,
            SerializableDeferrable = _serializableDeferrableCheckBox.Checked,
            UsePsqlRestrict = _usePsqlRestrictCheckBox.Checked,
            IncludeOwnership = _includeOwnershipCheckBox.Checked,
            IncludePrivileges = _includePrivilegesCheckBox.Checked,
            IncludeRoleSettings = _includeRoleSettingsCheckBox.Checked
        };
        AddSchemaNames(options.IncludeSchemas, _includeSchemasTextBox.Text);
        AddSchemaNames(options.ExcludeSchemas, _excludeSchemasTextBox.Text);
        return options;
    }

    /// <summary>
    /// <para>カンマ・セミコロン・改行で区切られたスキーマ名を集合へ追加します。</para>
    /// <para>Adds schema names separated by commas, semicolons, or newlines to a set.</para>
    /// </summary>
    /// <param name="destination">スキーマ名を追加する集合。 Set receiving schema names.</param>
    /// <param name="value">変換または解析する入力値。 Input value to format or parse.</param>
    private static void AddSchemaNames(ISet<string> destination, string value)
    {
        foreach (var schema in value.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            destination.Add(schema);
    }

    /// <summary>
    /// <para>管理用DBから接続を切断し、対象DBを削除して再作成します。</para>
    /// <para>Uses the maintenance database to terminate connections and drop and recreate the target.</para>
    /// </summary>
    /// <param name="database">対象DB名またはそのメタデータ。 Target database name or metadata.</param>
    /// <param name="maintenanceConnectionString">DB削除・作成に使用する管理用DBへの接続文字列。 Maintenance database connection string for dropping and creating the target.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private async Task RecreateDatabaseAsync(
        string database,
        string maintenanceConnectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync(cancellationToken);

        // 接続を切断してからDBを削除します。この再作成処理は復元トランザクションの外で行います。
        // Terminate sessions before dropping the database; recreation is outside the restore transaction.
        await using (var terminateCommand = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
            "WHERE datname = @database AND pid <> pg_backend_pid();", connection))
        {
            terminateCommand.Parameters.AddWithValue("database", database);
            await terminateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // DB名はSQLパラメーターにできない識別子なので、引用符をエスケープして埋め込みます。
        // Database names are identifiers, not SQL parameters; escape and quote them before interpolation.
        var quotedDatabase = QuoteIdentifier(database);
        await using (var dropCommand = new NpgsqlCommand($"DROP DATABASE IF EXISTS {quotedDatabase};", connection))
            await dropCommand.ExecuteNonQueryAsync(cancellationToken);
        await using (var createCommand = new NpgsqlCommand($"CREATE DATABASE {quotedDatabase};", connection))
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// <para>画面の接続情報と指定されたDB名から接続文字列を作成します。</para>
    /// <para>Builds a connection string from UI settings and the specified database.</para>
    /// </summary>
    /// <param name="database">対象DB名またはそのメタデータ。 Target database name or metadata.</param>
    /// <returns>入力値をエスケープしたNpgsql接続文字列。 An Npgsql connection string with escaped input values.</returns>
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

    /// <summary>
    /// <para>キャンセル・状態表示・例外表示を伴う非同期操作を実行します。</para>
    /// <para>Runs an asynchronous operation with cancellation, status, and exception handling.</para>
    /// </summary>
    /// <param name="workingMessage">操作中に表示するメッセージ。 Status message displayed during the operation.</param>
    /// <param name="operation">キャンセルトークンを受け取る非同期操作。 Asynchronous operation receiving a cancellation token.</param>
    /// <param name="successMessage">成功時に表示するメッセージ。 Message displayed on success.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
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

    /// <summary>
    /// <para>ホスト名とユーザー名の必須入力を検証します。</para>
    /// <para>Validates the required host and user fields.</para>
    /// </summary>
    /// <returns>ホスト名とユーザー名が入力されている場合はtrue。 True when host and user fields are populated.</returns>
    private bool ValidateCommonInputs() =>
        RequireText(_hostTextBox, "ホスト名") && RequireText(_userTextBox, "ユーザー名");

    /// <summary>
    /// <para>必須入力を確認し、空の場合はメッセージ表示とフォーカス移動を行います。</para>
    /// <para>Checks a required text field and displays a message and focuses it when empty.</para>
    /// </summary>
    /// <param name="textBox">入力または検証するテキストボックス。 Text box to populate or validate.</param>
    /// <param name="name">対象の名前。 Target name.</param>
    /// <returns>空白以外の入力がある場合はtrue。 True when the input contains non-whitespace text.</returns>
    private bool RequireText(TextBox textBox, string name)
    {
        if (!string.IsNullOrWhiteSpace(textBox.Text))
            return true;
        ShowError($"{name}を入力してください。");
        textBox.Focus();
        return false;
    }

    /// <summary>
    /// <para>操作ボタン、進捗表示、カーソル、状態メッセージを更新します。</para>
    /// <para>Updates operation buttons, progress, cursor, and status for the busy state.</para>
    /// </summary>
    /// <param name="busy">操作実行中かどうか。 Whether an operation is running.</param>
    /// <param name="message">表示するメッセージ。 Message to display.</param>
    private void SetBusy(bool busy, string message)
    {
        _dumpButton.Enabled = !busy;
        _restoreButton.Enabled = !busy;
        _cancelButton.Enabled = busy;
        _progressBar.Visible = busy;
        _statusLabel.Text = message;
        UseWaitCursor = busy;
    }

    /// <summary>
    /// <para>操作例外を状態欄とエラーダイアログに表示します。</para>
    /// <para>Displays an operation exception in the status field and error dialog.</para>
    /// </summary>
    /// <param name="exception">表示する例外。 Exception to display.</param>
    private void ShowException(Exception exception)
    {
        _statusLabel.Text = $"失敗: {exception.Message}";
        MessageBox.Show(this, exception.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>
    /// <para>入力エラーを状態欄と警告ダイアログに表示します。</para>
    /// <para>Displays an input error in the status field and warning dialog.</para>
    /// </summary>
    /// <param name="message">表示するメッセージ。 Message to display.</param>
    private void ShowError(string message)
    {
        _statusLabel.Text = message;
        MessageBox.Show(this, message, "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>
    /// <para>DB名を識別子として引用し、内部の二重引用符をエスケープします。</para>
    /// <para>Quotes a database identifier and escapes embedded double quotes.</para>
    /// </summary>
    /// <param name="value">変換または解析する入力値。 Input value to format or parse.</param>
    /// <returns>内部引用符を二重化した、引用済みDB識別子。 A quoted database identifier with doubled embedded quotes.</returns>
    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
