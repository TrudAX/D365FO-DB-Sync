namespace DBSyncTool
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            // Menu
            menuStrip = new MenuStrip();
            fileToolStripMenuItem = new ToolStripMenuItem();
            saveToolStripMenuItem = new ToolStripMenuItem();
            saveAsToolStripMenuItem = new ToolStripMenuItem();
            loadToolStripMenuItem = new ToolStripMenuItem();
            openConfigFolderToolStripMenuItem = new ToolStripMenuItem();
            toolStripSeparator1 = new ToolStripSeparator();
            exitToolStripMenuItem = new ToolStripMenuItem();
            helpToolStripMenuItem = new ToolStripMenuItem();
            checkForUpdatesToolStripMenuItem = new ToolStripMenuItem();
            toolStripSeparator2 = new ToolStripSeparator();
            aboutToolStripMenuItem = new ToolStripMenuItem();

            // Top configuration panel
            lblConfig = new Label();
            cmbConfig = new ComboBox();

            // TabControl
            tabControl = new TabControl();
            tabTables = new TabPage();
            tabConnection = new TabPage();

            // Tables tab controls - 4 columns
            grpCol1 = new GroupBox();
            lblTablesToInclude = new Label();
            txtTablesToInclude = new TextBox();

            grpCol2 = new GroupBox();
            lblTablesToExclude = new Label();
            txtTablesToExclude = new TextBox();

            grpCol3 = new GroupBox();
            lblStrategyOverrides = new Label();
            txtStrategyOverrides = new TextBox();

            grpCol4 = new GroupBox();
            lblDefaultRecordCount = new Label();
            nudDefaultRecordCount = new NumericUpDown();
            chkTruncateAll = new CheckBox();
            lblFieldsToExclude = new Label();
            txtFieldsToExclude = new TextBox();

            // Connection tab controls
            lblAlias = new Label();
            txtAlias = new TextBox();

            lblTier2 = new Label();
            lblTier2ServerDb = new Label();
            txtTier2ServerDb = new TextBox();
            btnParseTier2ConnString = new Button();
            lblTier2Username = new Label();
            txtTier2Username = new TextBox();
            lblTier2Password = new Label();
            txtTier2Password = new TextBox();
            lblTier2ConnTimeout = new Label();
            nudTier2ConnTimeout = new NumericUpDown();
            lblTier2CmdTimeout = new Label();
            nudTier2CmdTimeout = new NumericUpDown();

            lblAxDb = new Label();
            lblAxDbServerDb = new Label();
            txtAxDbServerDb = new TextBox();
            lblAxDbUsername = new Label();
            txtAxDbUsername = new TextBox();
            lblAxDbPassword = new Label();
            txtAxDbPassword = new TextBox();
            lblAxDbCmdTimeout = new Label();
            nudAxDbCmdTimeout = new NumericUpDown();

            lblExecution = new Label();
            lblParallelWorkers = new Label();
            nudParallelWorkers = new NumericUpDown();

            lblSystemExcludedTables = new Label();
            txtSystemExcludedTables = new TextBox();
            btnInitSystemExcludedTables = new Button();
            chkShowExcludedTables = new CheckBox();

            // Optimization settings
            lblOptimization = new Label();
            lblTruncateThreshold = new Label();
            nudTruncateThreshold = new NumericUpDown();
            lblTier2Timestamps = new Label();
            txtTier2Timestamps = new TextBox();
            lblAxDBTimestamps = new Label();
            txtAxDBTimestamps = new TextBox();
            btnClearTimestamps = new Button();

            // Post-Transfer SQL controls
            lblPostTransferSql = new Label();
            lblPostTransferSqlHelp = new Label();
            txtPostTransferSql = new TextBox();
            chkExecutePostTransferAuto = new CheckBox();
            btnExecutePostTransfer = new Button();

            // Action buttons
            btnPrepareTableList = new Button();
            btnProcessTables = new Button();
            btnRetryFailed = new Button();
            btnProcessSelected = new Button();
            btnRunAll = new Button();
            btnStop = new Button();
            btnCopyToClipboard = new Button();

            // Status
            lblStatus = new Label();

            // Data grid
            dgvTables = new DataGridView();

            // Summary label
            lblSummary = new Label();

            // Log panel
            grpLog = new GroupBox();
            txtLog = new TextBox();
            btnClearLog = new Button();

            tabControl.SuspendLayout();
            tabTables.SuspendLayout();
            tabConnection.SuspendLayout();
            grpCol1.SuspendLayout();
            grpCol2.SuspendLayout();
            grpCol3.SuspendLayout();
            grpCol4.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)nudDefaultRecordCount).BeginInit();
            ((System.ComponentModel.ISupportInitialize)nudTier2ConnTimeout).BeginInit();
            ((System.ComponentModel.ISupportInitialize)nudTier2CmdTimeout).BeginInit();
            ((System.ComponentModel.ISupportInitialize)nudAxDbCmdTimeout).BeginInit();
            ((System.ComponentModel.ISupportInitialize)nudParallelWorkers).BeginInit();
            ((System.ComponentModel.ISupportInitialize)nudTruncateThreshold).BeginInit();
            ((System.ComponentModel.ISupportInitialize)dgvTables).BeginInit();
            grpLog.SuspendLayout();
            menuStrip.SuspendLayout();
            SuspendLayout();

            // MenuStrip
            menuStrip.Items.AddRange(new ToolStripItem[] { fileToolStripMenuItem, helpToolStripMenuItem });
            menuStrip.Location = new Point(0, 0);
            menuStrip.Name = "menuStrip";
            menuStrip.Size = new Size(1421, 24);
            menuStrip.TabIndex = 0;
            menuStrip.Text = "menuStrip";

            // File Menu
            fileToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] {
                saveToolStripMenuItem,
                saveAsToolStripMenuItem,
                loadToolStripMenuItem,
                openConfigFolderToolStripMenuItem,
                toolStripSeparator1,
                exitToolStripMenuItem
            });
            fileToolStripMenuItem.Name = "fileToolStripMenuItem";
            fileToolStripMenuItem.Size = new Size(37, 20);
            fileToolStripMenuItem.Text = "&File";

            saveToolStripMenuItem.Name = "saveToolStripMenuItem";
            saveToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.S;
            saveToolStripMenuItem.Size = new Size(180, 22);
            saveToolStripMenuItem.Text = "&Save";
            saveToolStripMenuItem.Click += BtnSave_Click;

            saveAsToolStripMenuItem.Name = "saveAsToolStripMenuItem";
            saveAsToolStripMenuItem.Size = new Size(180, 22);
            saveAsToolStripMenuItem.Text = "Save &As...";
            saveAsToolStripMenuItem.Click += BtnSaveAs_Click;

            loadToolStripMenuItem.Name = "loadToolStripMenuItem";
            loadToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.O;
            loadToolStripMenuItem.Size = new Size(180, 22);
            loadToolStripMenuItem.Text = "&Load...";
            loadToolStripMenuItem.Click += BtnLoad_Click;

            openConfigFolderToolStripMenuItem.Name = "openConfigFolderToolStripMenuItem";
            openConfigFolderToolStripMenuItem.Size = new Size(180, 22);
            openConfigFolderToolStripMenuItem.Text = "Open Config &Folder";
            openConfigFolderToolStripMenuItem.Click += OpenConfigFolderToolStripMenuItem_Click;

            toolStripSeparator1.Name = "toolStripSeparator1";
            toolStripSeparator1.Size = new Size(177, 6);

            exitToolStripMenuItem.Name = "exitToolStripMenuItem";
            exitToolStripMenuItem.Size = new Size(180, 22);
            exitToolStripMenuItem.Text = "E&xit";
            exitToolStripMenuItem.Click += ExitToolStripMenuItem_Click;

            // Help Menu
            helpToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] {
                checkForUpdatesToolStripMenuItem,
                toolStripSeparator2,
                aboutToolStripMenuItem
            });
            helpToolStripMenuItem.Name = "helpToolStripMenuItem";
            helpToolStripMenuItem.Size = new Size(44, 20);
            helpToolStripMenuItem.Text = "&Help";

            checkForUpdatesToolStripMenuItem.Name = "checkForUpdatesToolStripMenuItem";
            checkForUpdatesToolStripMenuItem.Size = new Size(180, 22);
            checkForUpdatesToolStripMenuItem.Text = "Check for &Updates...";
            checkForUpdatesToolStripMenuItem.Click += CheckForUpdatesToolStripMenuItem_Click;

            toolStripSeparator2.Name = "toolStripSeparator2";
            toolStripSeparator2.Size = new Size(177, 6);

            aboutToolStripMenuItem.Name = "aboutToolStripMenuItem";
            aboutToolStripMenuItem.Size = new Size(180, 22);
            aboutToolStripMenuItem.Text = "&About...";
            aboutToolStripMenuItem.Click += AboutToolStripMenuItem_Click;

            // Configuration Panel (Top, outside tabs)
            lblConfig.AutoSize = true;
            lblConfig.Location = new Point(12, 32);
            lblConfig.Name = "lblConfig";
            lblConfig.Size = new Size(50, 15);
            lblConfig.Text = "Config:";

            cmbConfig.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbConfig.FormattingEnabled = true;
            cmbConfig.Location = new Point(68, 29);
            cmbConfig.Name = "cmbConfig";
            cmbConfig.Size = new Size(250, 23);
            cmbConfig.SelectedIndexChanged += CmbConfig_SelectedIndexChanged;

            // TabControl (fills form below config)
            tabControl.Controls.Add(tabTables);
            tabControl.Controls.Add(tabConnection);
            tabControl.Location = new Point(0, 60);
            tabControl.Name = "tabControl";
            tabControl.SelectedIndex = 0;
            tabControl.Size = new Size(1421, 832);
            tabControl.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            // Tables Tab - includes all controls except Config
            tabTables.Controls.Add(grpCol1);
            tabTables.Controls.Add(grpCol2);
            tabTables.Controls.Add(grpCol3);
            tabTables.Controls.Add(grpCol4);
            tabTables.Controls.Add(btnPrepareTableList);
            tabTables.Controls.Add(btnProcessTables);
            tabTables.Controls.Add(btnRetryFailed);
            tabTables.Controls.Add(btnProcessSelected);
            tabTables.Controls.Add(btnRunAll);
            tabTables.Controls.Add(btnStop);
            tabTables.Controls.Add(btnCopyToClipboard);
            tabTables.Controls.Add(lblStatus);
            tabTables.Controls.Add(dgvTables);
            tabTables.Controls.Add(lblSummary);
            tabTables.Controls.Add(grpLog);
            tabTables.Location = new Point(4, 24);
            tabTables.Name = "tabTables";
            tabTables.Padding = new Padding(3);
            tabTables.Size = new Size(1413, 804);
            tabTables.Text = "Tables";
            tabTables.UseVisualStyleBackColor = true;

            // Column 1: Tables to Copy
            grpCol1.Controls.Add(lblTablesToInclude);
            grpCol1.Controls.Add(txtTablesToInclude);
            grpCol1.Location = new Point(10, 10);
            grpCol1.Name = "grpCol1";
            grpCol1.Size = new Size(335, 200);
            grpCol1.Text = "Tables to Copy";

            lblTablesToInclude.AutoSize = true;
            lblTablesToInclude.Location = new Point(10, 25);
            lblTablesToInclude.Text = "Patterns (one per line):";

            txtTablesToInclude.Location = new Point(10, 45);
            txtTablesToInclude.Multiline = true;
            txtTablesToInclude.Name = "txtTablesToInclude";
            txtTablesToInclude.ScrollBars = ScrollBars.Both;
            txtTablesToInclude.WordWrap = false;
            txtTablesToInclude.Size = new Size(315, 145);

            // Column 2: Tables to Exclude
            grpCol2.Controls.Add(lblTablesToExclude);
            grpCol2.Controls.Add(txtTablesToExclude);
            grpCol2.Location = new Point(355, 10);
            grpCol2.Name = "grpCol2";
            grpCol2.Size = new Size(335, 200);
            grpCol2.Text = "Tables to Exclude";

            lblTablesToExclude.AutoSize = true;
            lblTablesToExclude.Location = new Point(10, 25);
            lblTablesToExclude.Text = "Patterns (one per line):";

            txtTablesToExclude.Location = new Point(10, 45);
            txtTablesToExclude.Multiline = true;
            txtTablesToExclude.Name = "txtTablesToExclude";
            txtTablesToExclude.ScrollBars = ScrollBars.Both;
            txtTablesToExclude.WordWrap = false;
            txtTablesToExclude.Size = new Size(315, 145);

            // Column 3: Copy strategy
            grpCol3.Controls.Add(lblStrategyOverrides);
            grpCol3.Controls.Add(txtStrategyOverrides);
            grpCol3.Location = new Point(700, 10);
            grpCol3.Name = "grpCol3";
            grpCol3.Size = new Size(335, 200);
            grpCol3.Text = "Copy strategy";

            lblStrategyOverrides.AutoSize = true;
            lblStrategyOverrides.Location = new Point(10, 25);
            lblStrategyOverrides.Text = "Per-Table Strategy (?)";
            ToolTip tooltip = new ToolTip();
            tooltip.SetToolTip(lblStrategyOverrides,
                "Simplified Strategy Syntax (one per line):\n\n" +
                "RecId Strategy:\n" +
                "  TABLENAME               Use default record count\n" +
                "  TABLENAME|5000          Top 5000 records by RecId DESC\n\n" +
                "SQL Strategy (custom query):\n" +
                "  TABLENAME|sql:SELECT * FROM TABLENAME WHERE DATAAREAID='1000'\n" +
                "  TABLENAME|5000|sql:SELECT * FROM TABLENAME WHERE POSTED=1\n\n" +
                "SQL Placeholders:\n" +
                "  *                     Replaced with actual field list\n" +
                "  @recordCount          Replaced with record count (default or specified)\n" +
                "  @sysRowVersionFilter  Replaced with SysRowVersion >= threshold AND RecId >= minRecId\n" +
                "                        (Required for SQL strategies to use INCREMENTAL optimization)\n\n" +
                "Flags:\n" +
                "  -truncate      Force TRUNCATE instead of delta comparison\n\n" +
                "Examples:\n" +
                "  CUSTTABLE\n" +
                "  SALESLINE|10000\n" +
                "  INVENTTRANS|sql:SELECT * FROM INVENTTRANS WHERE DATAAREAID='USMF'\n" +
                "  CUSTTRANS|5000|sql:SELECT TOP (@recordCount) * FROM CUSTTRANS WHERE BLOCKED=0\n" +
                "  VENDTABLE|5000 -truncate\n\n" +
                "Examples with optimization:\n" +
                "  INVENTDIM|50000|sql:SELECT * FROM INVENTDIM WHERE DATAAREAID='1000' AND @sysRowVersionFilter ORDER BY RecId DESC");

            txtStrategyOverrides.Location = new Point(10, 45);
            txtStrategyOverrides.Multiline = true;
            txtStrategyOverrides.Name = "txtStrategyOverrides";
            txtStrategyOverrides.ScrollBars = ScrollBars.Both;
            txtStrategyOverrides.WordWrap = false;
            txtStrategyOverrides.Size = new Size(315, 145);

            // Column 4: Default Records & Fields to Exclude
            grpCol4.Controls.Add(lblDefaultRecordCount);
            grpCol4.Controls.Add(nudDefaultRecordCount);
            grpCol4.Controls.Add(chkTruncateAll);
            grpCol4.Controls.Add(lblFieldsToExclude);
            grpCol4.Controls.Add(txtFieldsToExclude);
            grpCol4.Location = new Point(1045, 10);
            grpCol4.Name = "grpCol4";
            grpCol4.Size = new Size(335, 200);
            grpCol4.Text = "Other Settings";

            lblDefaultRecordCount.AutoSize = true;
            lblDefaultRecordCount.Location = new Point(10, 25);
            lblDefaultRecordCount.Text = "Records to copy:";

            nudDefaultRecordCount.Location = new Point(140, 23);
            nudDefaultRecordCount.Maximum = 1000000;
            nudDefaultRecordCount.Minimum = 1;
            nudDefaultRecordCount.Name = "nudDefaultRecordCount";
            nudDefaultRecordCount.Size = new Size(100, 23);
            nudDefaultRecordCount.Value = 10000;

            chkTruncateAll.AutoSize = true;
            chkTruncateAll.Location = new Point(10, 55);
            chkTruncateAll.Name = "chkTruncateAll";
            chkTruncateAll.Text = "Force truncate mode";
            ToolTip truncateTooltip = new ToolTip();
            truncateTooltip.SetToolTip(chkTruncateAll, "When checked, truncates all target tables before inserting data");

            lblFieldsToExclude.AutoSize = true;
            lblFieldsToExclude.Location = new Point(10, 85);
            lblFieldsToExclude.Text = "Fields to Exclude:";

            txtFieldsToExclude.Location = new Point(10, 105);
            txtFieldsToExclude.Multiline = true;
            txtFieldsToExclude.Name = "txtFieldsToExclude";
            txtFieldsToExclude.ScrollBars = ScrollBars.Vertical;
            txtFieldsToExclude.Size = new Size(315, 85);

            // Connection Tab
            tabConnection.Controls.Add(lblAlias);
            tabConnection.Controls.Add(txtAlias);
            tabConnection.Controls.Add(lblTier2);
            tabConnection.Controls.Add(lblTier2ServerDb);
            tabConnection.Controls.Add(txtTier2ServerDb);
            tabConnection.Controls.Add(btnParseTier2ConnString);
            tabConnection.Controls.Add(lblTier2Username);
            tabConnection.Controls.Add(txtTier2Username);
            tabConnection.Controls.Add(lblTier2Password);
            tabConnection.Controls.Add(txtTier2Password);
            tabConnection.Controls.Add(lblTier2ConnTimeout);
            tabConnection.Controls.Add(nudTier2ConnTimeout);
            tabConnection.Controls.Add(lblTier2CmdTimeout);
            tabConnection.Controls.Add(nudTier2CmdTimeout);
            tabConnection.Controls.Add(lblAxDb);
            tabConnection.Controls.Add(lblAxDbServerDb);
            tabConnection.Controls.Add(txtAxDbServerDb);
            tabConnection.Controls.Add(lblAxDbUsername);
            tabConnection.Controls.Add(txtAxDbUsername);
            tabConnection.Controls.Add(lblAxDbPassword);
            tabConnection.Controls.Add(txtAxDbPassword);
            tabConnection.Controls.Add(lblAxDbCmdTimeout);
            tabConnection.Controls.Add(nudAxDbCmdTimeout);
            tabConnection.Controls.Add(lblExecution);
            tabConnection.Controls.Add(lblParallelWorkers);
            tabConnection.Controls.Add(nudParallelWorkers);
            tabConnection.Controls.Add(lblSystemExcludedTables);
            tabConnection.Controls.Add(txtSystemExcludedTables);
            tabConnection.Controls.Add(btnInitSystemExcludedTables);
            tabConnection.Controls.Add(chkShowExcludedTables);
            tabConnection.Controls.Add(lblOptimization);
            tabConnection.Controls.Add(lblTruncateThreshold);
            tabConnection.Controls.Add(nudTruncateThreshold);
            tabConnection.Controls.Add(lblTier2Timestamps);
            tabConnection.Controls.Add(txtTier2Timestamps);
            tabConnection.Controls.Add(lblAxDBTimestamps);
            tabConnection.Controls.Add(txtAxDBTimestamps);
            tabConnection.Controls.Add(btnClearTimestamps);
            tabConnection.Controls.Add(lblPostTransferSql);
            tabConnection.Controls.Add(lblPostTransferSqlHelp);
            tabConnection.Controls.Add(txtPostTransferSql);
            tabConnection.Controls.Add(chkExecutePostTransferAuto);
            tabConnection.Controls.Add(btnExecutePostTransfer);
            tabConnection.Location = new Point(4, 24);
            tabConnection.Name = "tabConnection";
            tabConnection.Padding = new Padding(3);
            tabConnection.Size = new Size(1413, 804);
            tabConnection.Text = "Connection";
            tabConnection.UseVisualStyleBackColor = true;

            // Alias
            lblAlias.AutoSize = true;
            lblAlias.Location = new Point(10, 15);
            lblAlias.Text = "Alias:";

            txtAlias.Location = new Point(160, 12);
            txtAlias.MaxLength = 30;
            txtAlias.Name = "txtAlias";
            txtAlias.Size = new Size(300, 23);
            txtAlias.TextChanged += TxtAlias_TextChanged;

            // Tier2 Settings
            lblTier2.AutoSize = true;
            lblTier2.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            lblTier2.Location = new Point(10, 45);
            lblTier2.Text = "Tier2 Settings";

            lblTier2ServerDb.AutoSize = true;
            lblTier2ServerDb.Location = new Point(10, 70);
            lblTier2ServerDb.Text = "Server\\Database:";

            txtTier2ServerDb.Location = new Point(160, 67);
            txtTier2ServerDb.Name = "txtTier2ServerDb";
            txtTier2ServerDb.Size = new Size(600, 23);

            // Parse Connection String button (paste icon)
            btnParseTier2ConnString.Location = new Point(160, 45);
            btnParseTier2ConnString.Name = "btnParseTier2ConnString";
            btnParseTier2ConnString.Size = new Size(30, 23);
            btnParseTier2ConnString.Text = "📋";
            btnParseTier2ConnString.UseVisualStyleBackColor = true;
            btnParseTier2ConnString.Click += BtnParseTier2ConnString_Click;
            ToolTip parseTier2Tooltip = new ToolTip();
            parseTier2Tooltip.SetToolTip(btnParseTier2ConnString, "Paste connection string");

            lblTier2Username.AutoSize = true;
            lblTier2Username.Location = new Point(10, 100);
            lblTier2Username.Text = "Username:";

            txtTier2Username.Location = new Point(160, 97);
            txtTier2Username.Name = "txtTier2Username";
            txtTier2Username.Size = new Size(300, 23);

            lblTier2Password.AutoSize = true;
            lblTier2Password.Location = new Point(10, 130);
            lblTier2Password.Text = "Password:";

            txtTier2Password.Location = new Point(160, 127);
            txtTier2Password.Name = "txtTier2Password";
            txtTier2Password.PasswordChar = '*';
            txtTier2Password.Size = new Size(300, 23);

            lblTier2ConnTimeout.AutoSize = true;
            lblTier2ConnTimeout.Location = new Point(480, 100);
            lblTier2ConnTimeout.Text = "Connection Timeout (s):";

            nudTier2ConnTimeout.Location = new Point(630, 98);
            nudTier2ConnTimeout.Maximum = 300;
            nudTier2ConnTimeout.Minimum = 1;
            nudTier2ConnTimeout.Name = "nudTier2ConnTimeout";
            nudTier2ConnTimeout.Size = new Size(80, 23);
            nudTier2ConnTimeout.Value = 3;

            lblTier2CmdTimeout.AutoSize = true;
            lblTier2CmdTimeout.Location = new Point(480, 130);
            lblTier2CmdTimeout.Text = "Command Timeout (s):";

            nudTier2CmdTimeout.Location = new Point(630, 128);
            nudTier2CmdTimeout.Maximum = 3600;
            nudTier2CmdTimeout.Minimum = 0;
            nudTier2CmdTimeout.Name = "nudTier2CmdTimeout";
            nudTier2CmdTimeout.Size = new Size(80, 23);
            nudTier2CmdTimeout.Value = 600;

            // AxDB Settings
            lblAxDb.AutoSize = true;
            lblAxDb.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            lblAxDb.Location = new Point(800, 15);
            lblAxDb.Text = "AxDB Settings";

            lblAxDbServerDb.AutoSize = true;
            lblAxDbServerDb.Location = new Point(800, 40);
            lblAxDbServerDb.Text = "Server\\Database:";

            txtAxDbServerDb.Location = new Point(950, 37);
            txtAxDbServerDb.Name = "txtAxDbServerDb";
            txtAxDbServerDb.Size = new Size(180, 23);

            lblAxDbUsername.AutoSize = true;
            lblAxDbUsername.Location = new Point(800, 70);
            lblAxDbUsername.Text = "Username:";

            txtAxDbUsername.Location = new Point(950, 67);
            txtAxDbUsername.Name = "txtAxDbUsername";
            txtAxDbUsername.Size = new Size(180, 23);

            lblAxDbPassword.AutoSize = true;
            lblAxDbPassword.Location = new Point(800, 100);
            lblAxDbPassword.Text = "Password:";

            txtAxDbPassword.Location = new Point(950, 97);
            txtAxDbPassword.Name = "txtAxDbPassword";
            txtAxDbPassword.PasswordChar = '*';
            txtAxDbPassword.Size = new Size(180, 23);

            lblAxDbCmdTimeout.AutoSize = true;
            lblAxDbCmdTimeout.Location = new Point(800, 130);
            lblAxDbCmdTimeout.Text = "Command Timeout (s):";

            nudAxDbCmdTimeout.Location = new Point(950, 128);
            nudAxDbCmdTimeout.Maximum = 3600;
            nudAxDbCmdTimeout.Minimum = 0;
            nudAxDbCmdTimeout.Name = "nudAxDbCmdTimeout";
            nudAxDbCmdTimeout.Size = new Size(80, 23);
            nudAxDbCmdTimeout.Value = 0;

            // Execution Settings
            lblExecution.AutoSize = true;
            lblExecution.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            lblExecution.Location = new Point(10, 165);
            lblExecution.Text = "Execution Settings";

            lblParallelWorkers.AutoSize = true;
            lblParallelWorkers.Location = new Point(10, 190);
            lblParallelWorkers.Text = "Parallel Workers:";

            nudParallelWorkers.Location = new Point(150, 188);
            nudParallelWorkers.Maximum = 50;
            nudParallelWorkers.Minimum = 1;
            nudParallelWorkers.Name = "nudParallelWorkers";
            nudParallelWorkers.Size = new Size(80, 23);
            nudParallelWorkers.Value = 10;

            // System Excluded Tables
            lblSystemExcludedTables.AutoSize = true;
            lblSystemExcludedTables.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            lblSystemExcludedTables.Location = new Point(10, 225);
            lblSystemExcludedTables.Text = "System Excluded Tables";

            txtSystemExcludedTables.Location = new Point(10, 250);
            txtSystemExcludedTables.Multiline = true;
            txtSystemExcludedTables.Name = "txtSystemExcludedTables";
            txtSystemExcludedTables.ScrollBars = ScrollBars.Vertical;
            txtSystemExcludedTables.Size = new Size(400, 200);
            txtSystemExcludedTables.Font = new Font("Consolas", 9F);

            btnInitSystemExcludedTables.Location = new Point(200, 220);
            btnInitSystemExcludedTables.Name = "btnInitSystemExcludedTables";
            btnInitSystemExcludedTables.Size = new Size(50, 23);
            btnInitSystemExcludedTables.Text = "Init";
            btnInitSystemExcludedTables.Click += BtnInitSystemExcludedTables_Click;

            chkShowExcludedTables.AutoSize = true;
            chkShowExcludedTables.Location = new Point(10, 455);
            chkShowExcludedTables.Name = "chkShowExcludedTables";
            chkShowExcludedTables.Text = "Show excluded tables";
            ToolTip showExcludedTooltip = new ToolTip();
            showExcludedTooltip.SetToolTip(chkShowExcludedTables, "When checked, shows tables excluded by filters (with at least 1 record) in the table list with Status=Excluded");

            // SysRowVersion Optimization
            lblOptimization.AutoSize = true;
            lblOptimization.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            lblOptimization.Location = new Point(520, 225);
            lblOptimization.Text = "SysRowVersion Optimization";

            lblTruncateThreshold.AutoSize = true;
            lblTruncateThreshold.Location = new Point(520, 250);
            lblTruncateThreshold.Text = "Truncate Threshold %:";

            nudTruncateThreshold.Location = new Point(660, 248);
            nudTruncateThreshold.Maximum = 100;
            nudTruncateThreshold.Minimum = 1;
            nudTruncateThreshold.Name = "nudTruncateThreshold";
            nudTruncateThreshold.Size = new Size(80, 23);
            nudTruncateThreshold.Value = 40;

            lblTier2Timestamps.AutoSize = true;
            lblTier2Timestamps.Location = new Point(520, 285);
            lblTier2Timestamps.Text = "Tier2 Timestamps:";

            txtTier2Timestamps.Location = new Point(520, 310);
            txtTier2Timestamps.Multiline = true;
            txtTier2Timestamps.Name = "txtTier2Timestamps";
            txtTier2Timestamps.ScrollBars = ScrollBars.Vertical;
            txtTier2Timestamps.Size = new Size(400, 140);
            txtTier2Timestamps.Font = new Font("Consolas", 8F);

            lblAxDBTimestamps.AutoSize = true;
            lblAxDBTimestamps.Location = new Point(520, 460);
            lblAxDBTimestamps.Text = "AxDB Timestamps:";

            txtAxDBTimestamps.Location = new Point(520, 485);
            txtAxDBTimestamps.Multiline = true;
            txtAxDBTimestamps.Name = "txtAxDBTimestamps";
            txtAxDBTimestamps.ScrollBars = ScrollBars.Vertical;
            txtAxDBTimestamps.Size = new Size(400, 140);
            txtAxDBTimestamps.Font = new Font("Consolas", 8F);

            btnClearTimestamps.Location = new Point(820, 635);
            btnClearTimestamps.Name = "btnClearTimestamps";
            btnClearTimestamps.Size = new Size(100, 30);
            btnClearTimestamps.Text = "Clear All";
            btnClearTimestamps.UseVisualStyleBackColor = true;
            btnClearTimestamps.Click += BtnClearTimestamps_Click;

            // Post-Transfer SQL Scripts
            lblPostTransferSql.AutoSize = true;
            lblPostTransferSql.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            lblPostTransferSql.Location = new Point(10, 490);
            lblPostTransferSql.Text = "AxDB Post-Transfer SQL Scripts";

            lblPostTransferSqlHelp.AutoSize = true;
            lblPostTransferSqlHelp.ForeColor = Color.Gray;
            lblPostTransferSqlHelp.Location = new Point(10, 515);
            lblPostTransferSqlHelp.Text = "SQL commands (one per line). Lines starting with -- are comments and skipped.";

            txtPostTransferSql.Location = new Point(10, 540);
            txtPostTransferSql.Multiline = true;
            txtPostTransferSql.Name = "txtPostTransferSql";
            txtPostTransferSql.ScrollBars = ScrollBars.Vertical;
            txtPostTransferSql.Size = new Size(500, 120);
            txtPostTransferSql.Font = new Font("Consolas", 9F);

            chkExecutePostTransferAuto.AutoSize = true;
            chkExecutePostTransferAuto.Location = new Point(10, 670);
            chkExecutePostTransferAuto.Name = "chkExecutePostTransferAuto";
            chkExecutePostTransferAuto.Text = "Execute automatically after successful transfer";

            btnExecutePostTransfer.Location = new Point(420, 665);
            btnExecutePostTransfer.Name = "btnExecutePostTransfer";
            btnExecutePostTransfer.Size = new Size(90, 30);
            btnExecutePostTransfer.Text = "Execute";
            btnExecutePostTransfer.UseVisualStyleBackColor = true;
            btnExecutePostTransfer.Click += BtnExecutePostTransfer_Click;

            // Action Buttons (in Tables tab)
            btnPrepareTableList.Location = new Point(12, 250);
            btnPrepareTableList.Name = "btnPrepareTableList";
            btnPrepareTableList.Size = new Size(130, 30);
            btnPrepareTableList.Text = "Discover Tables";
            btnPrepareTableList.Click += BtnPrepareTableList_Click;

            btnProcessTables.Location = new Point(152, 250);
            btnProcessTables.Name = "btnProcessTables";
            btnProcessTables.Size = new Size(120, 30);
            btnProcessTables.Text = "Process Tables";
            btnProcessTables.Click += BtnProcessTables_Click;

            btnRetryFailed.Location = new Point(282, 250);
            btnRetryFailed.Name = "btnRetryFailed";
            btnRetryFailed.Size = new Size(100, 30);
            btnRetryFailed.Text = "Retry Failed";
            btnRetryFailed.Click += BtnRetryFailed_Click;

            btnProcessSelected.Location = new Point(392, 250);
            btnProcessSelected.Name = "btnProcessSelected";
            btnProcessSelected.Size = new Size(120, 30);
            btnProcessSelected.Text = "Process Selected";
            btnProcessSelected.Click += BtnProcessSelected_Click;

            btnRunAll.Location = new Point(522, 250);
            btnRunAll.Name = "btnRunAll";
            btnRunAll.Size = new Size(100, 30);
            btnRunAll.Text = "Run All";
            btnRunAll.Click += BtnRunAll_Click;

            btnStop.Location = new Point(632, 250);
            btnStop.Name = "btnStop";
            btnStop.Size = new Size(100, 30);
            btnStop.Text = "Stop";
            btnStop.Enabled = false;
            btnStop.Click += BtnStop_Click;

            btnCopyToClipboard.Location = new Point(742, 250);
            btnCopyToClipboard.Name = "btnCopyToClipboard";
            btnCopyToClipboard.Size = new Size(140, 30);
            btnCopyToClipboard.Text = "Copy to Clipboard";
            btnCopyToClipboard.Click += BtnCopyToClipboard_Click;

            // Status Label (in Tables tab)
            lblStatus.AutoSize = true;
            lblStatus.Location = new Point(12, 290);
            lblStatus.Name = "lblStatus";
            lblStatus.Size = new Size(50, 15);
            lblStatus.Text = "Ready";
            lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            // Data Grid (in Tables tab)
            dgvTables.AllowUserToAddRows = false;
            dgvTables.AllowUserToDeleteRows = false;
            dgvTables.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            dgvTables.Location = new Point(12, 315);
            dgvTables.Name = "dgvTables";
            dgvTables.ReadOnly = true;
            dgvTables.RowHeadersVisible = false;
            dgvTables.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dgvTables.Size = new Size(1360, 300);
            dgvTables.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            dgvTables.SelectionChanged += DgvTables_SelectionChanged;

            // Summary Label (in Tables tab)
            lblSummary.AutoSize = true;
            lblSummary.Location = new Point(12, 623);
            lblSummary.Name = "lblSummary";
            lblSummary.Size = new Size(0, 15);
            lblSummary.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            // Log Panel (in Tables tab)
            grpLog.Controls.Add(btnClearLog);
            grpLog.Controls.Add(txtLog);
            grpLog.Location = new Point(12, 645);
            grpLog.Name = "grpLog";
            grpLog.Size = new Size(1360, 145);
            grpLog.Text = "Log";
            grpLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            // Clear button - small, on the right
            btnClearLog.Location = new Point(1320, 20);
            btnClearLog.Name = "btnClearLog";
            btnClearLog.Size = new Size(30, 25);
            btnClearLog.Text = "×";
            btnClearLog.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            btnClearLog.ForeColor = Color.Red;
            btnClearLog.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnClearLog.Click += BtnClearLog_Click;
            ToolTip clearTooltip = new ToolTip();
            clearTooltip.SetToolTip(btnClearLog, "Clear Log");

            // Log text box
            txtLog.Location = new Point(10, 20);
            txtLog.Multiline = true;
            txtLog.Name = "txtLog";
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Size = new Size(1305, 115);
            txtLog.Font = new Font("Consolas", 9F);
            txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            // MainForm
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1421, 892);
            Controls.Add(lblConfig);
            Controls.Add(cmbConfig);
            Controls.Add(tabControl);
            Controls.Add(menuStrip);
            MainMenuStrip = menuStrip;
            MinimumSize = new Size(1437, 650);
            Name = "MainForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "D365FO Database Sync Tool";
            Icon = new Icon(Path.Combine(Application.StartupPath, "app.ico"));

            tabControl.ResumeLayout(false);
            tabTables.ResumeLayout(false);
            tabConnection.ResumeLayout(false);
            grpCol1.ResumeLayout(false);
            grpCol1.PerformLayout();
            grpCol2.ResumeLayout(false);
            grpCol2.PerformLayout();
            grpCol3.ResumeLayout(false);
            grpCol3.PerformLayout();
            grpCol4.ResumeLayout(false);
            grpCol4.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)nudDefaultRecordCount).EndInit();
            ((System.ComponentModel.ISupportInitialize)nudTier2ConnTimeout).EndInit();
            ((System.ComponentModel.ISupportInitialize)nudTier2CmdTimeout).EndInit();
            ((System.ComponentModel.ISupportInitialize)nudAxDbCmdTimeout).EndInit();
            ((System.ComponentModel.ISupportInitialize)nudParallelWorkers).EndInit();
            ((System.ComponentModel.ISupportInitialize)nudTruncateThreshold).EndInit();
            ((System.ComponentModel.ISupportInitialize)dgvTables).EndInit();
            grpLog.ResumeLayout(false);
            grpLog.PerformLayout();
            menuStrip.ResumeLayout(false);
            menuStrip.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        // Menu
        private MenuStrip menuStrip;
        private ToolStripMenuItem fileToolStripMenuItem;
        private ToolStripMenuItem saveToolStripMenuItem;
        private ToolStripMenuItem saveAsToolStripMenuItem;
        private ToolStripMenuItem loadToolStripMenuItem;
        private ToolStripMenuItem openConfigFolderToolStripMenuItem;
        private ToolStripSeparator toolStripSeparator1;
        private ToolStripMenuItem exitToolStripMenuItem;
        private ToolStripMenuItem helpToolStripMenuItem;
        private ToolStripMenuItem checkForUpdatesToolStripMenuItem;
        private ToolStripSeparator toolStripSeparator2;
        private ToolStripMenuItem aboutToolStripMenuItem;

        // Configuration Panel
        private Label lblConfig;
        private ComboBox cmbConfig;

        // TabControl
        private TabControl tabControl;
        private TabPage tabTables;
        private TabPage tabConnection;

        // Tables Tab - 4 columns
        private GroupBox grpCol1;
        private Label lblTablesToInclude;
        private TextBox txtTablesToInclude;

        private GroupBox grpCol2;
        private Label lblTablesToExclude;
        private TextBox txtTablesToExclude;

        private GroupBox grpCol3;
        private Label lblStrategyOverrides;
        private TextBox txtStrategyOverrides;

        private GroupBox grpCol4;
        private Label lblDefaultRecordCount;
        private NumericUpDown nudDefaultRecordCount;
        private CheckBox chkTruncateAll;
        private Label lblFieldsToExclude;
        private TextBox txtFieldsToExclude;

        // Connection Tab
        private Label lblAlias;
        private TextBox txtAlias;
        private Label lblTier2;
        private Label lblTier2ServerDb;
        private TextBox txtTier2ServerDb;
        private Button btnParseTier2ConnString;
        private Label lblTier2Username;
        private TextBox txtTier2Username;
        private Label lblTier2Password;
        private TextBox txtTier2Password;
        private Label lblTier2ConnTimeout;
        private NumericUpDown nudTier2ConnTimeout;
        private Label lblTier2CmdTimeout;
        private NumericUpDown nudTier2CmdTimeout;

        private Label lblAxDb;
        private Label lblAxDbServerDb;
        private TextBox txtAxDbServerDb;
        private Label lblAxDbUsername;
        private TextBox txtAxDbUsername;
        private Label lblAxDbPassword;
        private TextBox txtAxDbPassword;
        private Label lblAxDbCmdTimeout;
        private NumericUpDown nudAxDbCmdTimeout;

        private Label lblExecution;
        private Label lblParallelWorkers;
        private NumericUpDown nudParallelWorkers;

        private Label lblSystemExcludedTables;
        private TextBox txtSystemExcludedTables;
        private Button btnInitSystemExcludedTables;
        private CheckBox chkShowExcludedTables;

        // Optimization controls
        private Label lblOptimization;
        private Label lblTruncateThreshold;
        private NumericUpDown nudTruncateThreshold;
        private Label lblTier2Timestamps;
        private TextBox txtTier2Timestamps;
        private Label lblAxDBTimestamps;
        private TextBox txtAxDBTimestamps;
        private Button btnClearTimestamps;

        // Post-Transfer SQL controls
        private Label lblPostTransferSql;
        private Label lblPostTransferSqlHelp;
        private TextBox txtPostTransferSql;
        private CheckBox chkExecutePostTransferAuto;
        private Button btnExecutePostTransfer;

        // Action Buttons
        private Button btnPrepareTableList;
        private Button btnProcessTables;
        private Button btnRetryFailed;
        private Button btnProcessSelected;
        private Button btnRunAll;
        private Button btnStop;
        private Button btnCopyToClipboard;

        // Status and Grid
        private Label lblStatus;
        private DataGridView dgvTables;
        private Label lblSummary;

        // Log Panel
        private GroupBox grpLog;
        private TextBox txtLog;
        private Button btnClearLog;
    }
}
