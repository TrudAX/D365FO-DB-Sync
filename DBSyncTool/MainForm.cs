using DBSyncTool.Helpers;
using DBSyncTool.Models;
using DBSyncTool.Services;
using System.ComponentModel;

namespace DBSyncTool
{
    public partial class MainForm : Form
    {
        private AppConfiguration _currentConfig = null!;
        private ConfigManager _configManager;
        private CopyOrchestrator? _orchestrator;
        private BindingList<TableInfo> _tablesBindingList;
        private bool _isExecuting = false;
        private bool _isUpdatingComboBox = false;
        private bool _timestampsUpdatedDuringExecution = false;
        private System.Windows.Forms.Timer _updateTimer;

        public MainForm()
        {
            InitializeComponent();
            _configManager = new ConfigManager();
            _tablesBindingList = new BindingList<TableInfo>();

            // Initialize timer for UI updates
            _updateTimer = new System.Windows.Forms.Timer();
            _updateTimer.Interval = 3000;
            _updateTimer.Tick += UpdateTimer_Tick;

            InitializeDataGrid();
            LoadInitialConfiguration();
            UpdateButtonStates();

            // Register form closing event for cleanup
            this.FormClosing += MainForm_FormClosing;
        }

        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            // Cleanup orchestrator and event handlers
            UnsubscribeOrchestratorEvents();
            _orchestrator = null;

            // Clear binding list to release table references
            _tablesBindingList.Clear();

            // Stop and dispose update timer
            _updateTimer.Stop();
            _updateTimer.Dispose();
        }

        private void InitializeDataGrid()
        {
            dgvTables.AutoGenerateColumns = false;
            dgvTables.DataSource = _tablesBindingList;

            // Enable sorting and column resizing (but not row resizing)
            dgvTables.AllowUserToOrderColumns = true;
            dgvTables.AllowUserToResizeColumns = true;
            dgvTables.AllowUserToResizeRows = false;

            // Add column header click event for sorting
            dgvTables.ColumnHeaderMouseClick += DgvTables_ColumnHeaderMouseClick;

            // Add context menu for copying table name and getting SQL
            var contextMenu = new ContextMenuStrip();

            var copyTableNameItem = new ToolStripMenuItem("Copy Table Name");
            copyTableNameItem.Click += CopyTableName_Click;
            contextMenu.Items.Add(copyTableNameItem);

            contextMenu.Items.Add(new ToolStripSeparator());

            var getSqlItem = new ToolStripMenuItem("Get SQL");
            getSqlItem.Click += GetSql_Click;
            contextMenu.Items.Add(getSqlItem);

            dgvTables.ContextMenuStrip = contextMenu;

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "TableName",
                HeaderText = "Table Name",
                Name = "TableName",
                Width = 150,
                SortMode = DataGridViewColumnSortMode.Automatic
            });

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "TableId",
                HeaderText = "TableID",
                Name = "TableId",
                Width = 80,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
                SortMode = DataGridViewColumnSortMode.Automatic
            });

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "StrategyDisplay",
                HeaderText = "Strategy",
                Name = "Strategy",
                Width = 100,
                SortMode = DataGridViewColumnSortMode.Automatic
            });

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "EstimatedSizeMBDisplay",
                HeaderText = "Est Size (MB)",
                Name = "EstimatedSizeMB",
                Width = 100,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
                SortMode = DataGridViewColumnSortMode.Automatic
            });

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "Tier2RowCountDisplay",
                HeaderText = "Tier2 Rows",
                Name = "Tier2Rows",
                Width = 100,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
                SortMode = DataGridViewColumnSortMode.Automatic
            });

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "Tier2SizeGBDisplay",
                HeaderText = "Tier2 Size (GB)",
                Name = "Tier2Size",
                Width = 80,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
                SortMode = DataGridViewColumnSortMode.Automatic
            });

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "Status",
                HeaderText = "Status",
                Name = "Status",
                Width = 100,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter },
                SortMode = DataGridViewColumnSortMode.Automatic
            });

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "RecordsFetched",
                HeaderText = "Records Fetched",
                Name = "RecordsFetched",
                Width = 100,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
                SortMode = DataGridViewColumnSortMode.Automatic
            });

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "MinRecId",
                HeaderText = "Min RecId",
                Name = "MinRecId",
                Width = 120,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
                SortMode = DataGridViewColumnSortMode.Automatic
            });

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "FetchTimeDisplay",
                HeaderText = "Fetch Time (s)",
                Name = "FetchTime",
                Width = 80,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
                SortMode = DataGridViewColumnSortMode.Automatic
            });

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "DeleteTimeDisplay",
                HeaderText = "Delete Time (s)",
                Name = "DeleteTime",
                Width = 80,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
                SortMode = DataGridViewColumnSortMode.Automatic
            });

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "InsertTimeDisplay",
                HeaderText = "Insert Time (s)",
                Name = "InsertTime",
                Width = 80,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
                SortMode = DataGridViewColumnSortMode.Automatic
            });

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "CompareTimeDisplay",
                HeaderText = "Compare (s)",
                Name = "CompareTime",
                Width = 80,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
                SortMode = DataGridViewColumnSortMode.Automatic
            });

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "UnchangedDisplay",
                HeaderText = "Unchanged",
                Name = "Unchanged",
                Width = 80,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
                SortMode = DataGridViewColumnSortMode.Automatic
            });

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "ModifiedDisplay",
                HeaderText = "Modified",
                Name = "Modified",
                Width = 70,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
                SortMode = DataGridViewColumnSortMode.Automatic
            });

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "NewInTier2Display",
                HeaderText = "New",
                Name = "New",
                Width = 70,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
                SortMode = DataGridViewColumnSortMode.Automatic
            });

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "DeletedFromAxDbDisplay",
                HeaderText = "Deleted",
                Name = "Deleted",
                Width = 70,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
                SortMode = DataGridViewColumnSortMode.Automatic
            });

            dgvTables.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "Error",
                HeaderText = "Error",
                Name = "Error",
                Width = 200,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                SortMode = DataGridViewColumnSortMode.Automatic
            });
        }

        private void LoadInitialConfiguration()
        {
            try
            {
                _currentConfig = _configManager.LoadLastOrDefault();
                LoadConfigurationIntoUI();
                RefreshConfigDropdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading configuration: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _currentConfig = AppConfiguration.CreateDefault();
                LoadConfigurationIntoUI();
            }
        }

        private void LoadConfigurationIntoUI()
        {
            // Connection tab
            txtAlias.Text = _currentConfig.Alias;
            txtTier2ServerDb.Text = _currentConfig.Tier2Connection.ServerDatabase;
            txtTier2Username.Text = _currentConfig.Tier2Connection.Username;
            txtTier2Password.Text = _currentConfig.Tier2Connection.Password;
            nudTier2ConnTimeout.Value = _currentConfig.Tier2Connection.ConnectionTimeout;
            nudTier2CmdTimeout.Value = _currentConfig.Tier2Connection.CommandTimeout;

            txtAxDbServerDb.Text = _currentConfig.AxDbConnection.ServerDatabase;
            txtAxDbUsername.Text = _currentConfig.AxDbConnection.Username;
            txtAxDbPassword.Text = _currentConfig.AxDbConnection.Password;
            nudAxDbCmdTimeout.Value = _currentConfig.AxDbConnection.CommandTimeout;

            nudParallelWorkers.Value = _currentConfig.ParallelWorkers;

            txtSystemExcludedTables.Text = _currentConfig.SystemExcludedTables;
            chkShowExcludedTables.Checked = _currentConfig.ShowExcludedTables;

            // Optimization settings
            nudTruncateThreshold.Value = _currentConfig.TruncateThresholdPercent;
            txtTier2Timestamps.Text = _currentConfig.Tier2Timestamps;
            txtAxDBTimestamps.Text = _currentConfig.AxDBTimestamps;

            // Tables tab
            txtTablesToInclude.Text = _currentConfig.TablesToInclude;
            txtTablesToExclude.Text = _currentConfig.TablesToExclude;
            txtFieldsToExclude.Text = _currentConfig.FieldsToExclude;
            nudDefaultRecordCount.Value = _currentConfig.DefaultRecordCount;
            chkTruncateAll.Checked = _currentConfig.TruncateAllTables;
            txtStrategyOverrides.Text = _currentConfig.StrategyOverrides;

            UpdateConnectionTabTitle();

            // Initialize system excluded tables if empty (new configuration)
            if (string.IsNullOrWhiteSpace(_currentConfig.SystemExcludedTables))
            {
                InitializeSystemExcludedTables();
            }
        }

        private void SaveConfigurationFromUI()
        {
            _currentConfig.Alias = txtAlias.Text;
            _currentConfig.Tier2Connection.ServerDatabase = txtTier2ServerDb.Text;
            _currentConfig.Tier2Connection.Username = txtTier2Username.Text;
            _currentConfig.Tier2Connection.Password = txtTier2Password.Text;
            _currentConfig.Tier2Connection.ConnectionTimeout = (int)nudTier2ConnTimeout.Value;
            _currentConfig.Tier2Connection.CommandTimeout = (int)nudTier2CmdTimeout.Value;

            _currentConfig.AxDbConnection.ServerDatabase = txtAxDbServerDb.Text;
            _currentConfig.AxDbConnection.Username = txtAxDbUsername.Text;
            _currentConfig.AxDbConnection.Password = txtAxDbPassword.Text;
            _currentConfig.AxDbConnection.CommandTimeout = (int)nudAxDbCmdTimeout.Value;

            _currentConfig.ParallelWorkers = (int)nudParallelWorkers.Value;

            _currentConfig.SystemExcludedTables = txtSystemExcludedTables.Text;
            _currentConfig.ShowExcludedTables = chkShowExcludedTables.Checked;

            // Optimization settings
            _currentConfig.TruncateThresholdPercent = (int)nudTruncateThreshold.Value;
            _currentConfig.Tier2Timestamps = txtTier2Timestamps.Text;
            _currentConfig.AxDBTimestamps = txtAxDBTimestamps.Text;

            _currentConfig.TablesToInclude = txtTablesToInclude.Text;
            _currentConfig.TablesToExclude = txtTablesToExclude.Text;
            _currentConfig.FieldsToExclude = txtFieldsToExclude.Text;
            _currentConfig.DefaultRecordCount = (int)nudDefaultRecordCount.Value;
            _currentConfig.TruncateAllTables = chkTruncateAll.Checked;
            _currentConfig.StrategyOverrides = txtStrategyOverrides.Text;
        }

        private void RefreshTimestampUI()
        {
            // Update timestamp textboxes from config (they may have been updated by CopyOrchestrator)
            if (InvokeRequired)
            {
                Invoke(new Action(() =>
                {
                    txtTier2Timestamps.Text = _currentConfig.Tier2Timestamps;
                    txtAxDBTimestamps.Text = _currentConfig.AxDBTimestamps;
                }));
            }
            else
            {
                txtTier2Timestamps.Text = _currentConfig.Tier2Timestamps;
                txtAxDBTimestamps.Text = _currentConfig.AxDBTimestamps;
            }
        }

        private void RefreshConfigDropdown()
        {
            _isUpdatingComboBox = true;
            try
            {
                var configs = _configManager.GetAvailableConfigurations();
                cmbConfig.Items.Clear();
                foreach (var config in configs)
                {
                    cmbConfig.Items.Add(config);
                }

                if (configs.Contains(_currentConfig.ConfigName))
                {
                    cmbConfig.SelectedItem = _currentConfig.ConfigName;
                }
            }
            finally
            {
                _isUpdatingComboBox = false;
            }
        }

        private void UpdateConnectionTabTitle()
        {
            tabConnection.Text = $"Connection-{_currentConfig.Alias}";
        }

        private void UpdateButtonStates()
        {
            bool hasPendingTables = _orchestrator != null &&
                _orchestrator.GetTables().Any(t => t.Status == TableStatus.Pending);
            bool hasFailedTables = _orchestrator != null &&
                _orchestrator.GetTables().Any(t => t.Status == TableStatus.FetchError ||
                                                  t.Status == TableStatus.InsertError);
            bool hasSelection = dgvTables.SelectedRows.Count > 0;

            btnPrepareTableList.Enabled = !_isExecuting;
            btnProcessTables.Enabled = !_isExecuting && hasPendingTables;
            btnRetryFailed.Enabled = !_isExecuting && hasFailedTables;
            btnProcessSelected.Enabled = !_isExecuting && hasSelection && _orchestrator != null;
            btnRunAll.Enabled = !_isExecuting;
            btnStop.Enabled = _isExecuting;

            // Menu items enabled/disabled state
            saveToolStripMenuItem.Enabled = !_isExecuting;
            saveAsToolStripMenuItem.Enabled = !_isExecuting;
            loadToolStripMenuItem.Enabled = !_isExecuting;
        }

        private void Log(string message)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string>(Log), message);
                return;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            txtLog.AppendText($"[{timestamp}] {message}\r\n");
        }

        private void UpdateStatus(string status)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string>(UpdateStatus), status);
                return;
            }

            lblStatus.Text = status;
        }

        private void UpdateTablesGrid(List<TableInfo> tables)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<List<TableInfo>>(UpdateTablesGrid), tables);
                return;
            }

            // Save current selection and scroll position
            int selectedRowIndex = dgvTables.SelectedRows.Count > 0
                ? dgvTables.SelectedRows[0].Index
                : -1;
            int firstDisplayedScrollingRowIndex = dgvTables.FirstDisplayedScrollingRowIndex;

            _tablesBindingList.Clear();
            foreach (var table in tables)
            {
                _tablesBindingList.Add(table);
            }

            // Restore selection and scroll position
            if (selectedRowIndex >= 0 && selectedRowIndex < dgvTables.Rows.Count)
            {
                dgvTables.ClearSelection();
                dgvTables.Rows[selectedRowIndex].Selected = true;
            }

            if (firstDisplayedScrollingRowIndex >= 0 && firstDisplayedScrollingRowIndex < dgvTables.Rows.Count)
            {
                dgvTables.FirstDisplayedScrollingRowIndex = firstDisplayedScrollingRowIndex;
            }

            UpdateSummary(tables);
        }

        private void UpdateSummary(List<TableInfo> tables)
        {
            int completed = tables.Count(t => t.Status == TableStatus.Inserted);
            int failed = tables.Count(t => t.Status == TableStatus.InsertError || t.Status == TableStatus.FetchError);

            if (tables.Count > 0)
            {
                lblSummary.Text = $"Loaded {tables.Count} tables, {completed} inserted, {failed} failed";
            }
            else
            {
                lblSummary.Text = "";
            }
        }

        // ========== Event Handlers ==========

        private void TxtAlias_TextChanged(object sender, EventArgs e)
        {
            UpdateConnectionTabTitle();
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            try
            {
                SaveConfigurationFromUI();
                _configManager.SaveConfiguration(_currentConfig);
                Log($"Configuration '{_currentConfig.ConfigName}' saved");
                MessageBox.Show("Configuration saved successfully", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving configuration: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnSaveAs_Click(object sender, EventArgs e)
        {
            string defaultName = txtAlias.Text;
            string? newName = PromptForConfigName("Enter new configuration name:", defaultName);

            if (!string.IsNullOrWhiteSpace(newName))
            {
                try
                {
                    SaveConfigurationFromUI();
                    _currentConfig.ConfigName = newName;
                    _configManager.SaveConfiguration(_currentConfig);
                    RefreshConfigDropdown();
                    Log($"Configuration saved as '{newName}'");
                    MessageBox.Show($"Configuration saved as '{newName}'", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error saving configuration: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnLoad_Click(object sender, EventArgs e)
        {
            // Only refresh if triggered by the Load menu item (not by SelectedIndexChanged)
            bool isLoadMenuItem = sender == loadToolStripMenuItem;
            if (isLoadMenuItem)
            {
                RefreshConfigDropdown();
            }

            if (cmbConfig.Items.Count == 0)
            {
                MessageBox.Show("No configurations available", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (cmbConfig.SelectedItem != null)
            {
                string configName = cmbConfig.SelectedItem.ToString()!;
                try
                {
                    _currentConfig = _configManager.LoadConfiguration(configName);
                    LoadConfigurationIntoUI();
                    Log($"Configuration '{configName}' loaded");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading configuration: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void CmbConfig_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Auto-load when selection changes (but not when programmatically updating)
            if (cmbConfig.SelectedItem != null && !_isExecuting && !_isUpdatingComboBox)
            {
                BtnLoad_Click(sender, e);
            }
        }

        private async void BtnPrepareTableList_Click(object sender, EventArgs e)
        {
            await ExecuteOperationAsync(async () =>
            {
                SaveConfigurationFromUI();
                UnsubscribeOrchestratorEvents();
                _orchestrator = new CopyOrchestrator(_currentConfig, Log);
                _orchestrator.TablesUpdated += Orchestrator_TablesUpdated;
                _orchestrator.StatusUpdated += Orchestrator_StatusUpdated;
                _orchestrator.TimestampsUpdated += Orchestrator_TimestampsUpdated;

                await _orchestrator.PrepareTableListAsync();
            });
        }

        private async void BtnProcessTables_Click(object sender, EventArgs e)
        {
            await ExecuteOperationAsync(async () =>
            {
                if (_orchestrator != null)
                {
                    await _orchestrator.ProcessTablesAsync();
                }
            });
        }

        private async void BtnRetryFailed_Click(object sender, EventArgs e)
        {
            await ExecuteOperationAsync(async () =>
            {
                if (_orchestrator != null)
                {
                    // Save current configuration from UI to refresh Tier2 connection
                    SaveConfigurationFromUI();
                    await _orchestrator.RetryFailedAsync();
                }
            });
        }

        private async void BtnProcessSelected_Click(object sender, EventArgs e)
        {
            if (dgvTables.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select a table to process", "No Selection",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var selectedRow = dgvTables.SelectedRows[0];
            var tableInfo = selectedRow.DataBoundItem as TableInfo;

            if (tableInfo == null)
            {
                return;
            }

            await ExecuteOperationAsync(async () =>
            {
                if (_orchestrator != null)
                {
                    // Save current configuration from UI to apply latest strategy settings
                    SaveConfigurationFromUI();
                    await _orchestrator.ProcessSingleTableByNameAsync(tableInfo.TableName);
                }
            });
        }

        private async void BtnRunAll_Click(object sender, EventArgs e)
        {
            await ExecuteOperationAsync(async () =>
            {
                SaveConfigurationFromUI();
                UnsubscribeOrchestratorEvents();
                _orchestrator = new CopyOrchestrator(_currentConfig, Log);
                _orchestrator.TablesUpdated += Orchestrator_TablesUpdated;
                _orchestrator.StatusUpdated += Orchestrator_StatusUpdated;
                _orchestrator.TimestampsUpdated += Orchestrator_TimestampsUpdated;

                await _orchestrator.RunAllStagesAsync();
            });
        }

        private void BtnStop_Click(object sender, EventArgs e)
        {
            _orchestrator?.Stop();
            Log("Stop requested by user");
        }

        private void BtnClearLog_Click(object sender, EventArgs e)
        {
            txtLog.Clear();
        }

        private void BtnCopyToClipboard_Click(object sender, EventArgs e)
        {
            try
            {
                if (_orchestrator == null || _orchestrator.GetTables().Count == 0)
                {
                    MessageBox.Show("No data to copy", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var sb = new System.Text.StringBuilder();

                // Add header row
                var headers = new List<string>();
                foreach (DataGridViewColumn column in dgvTables.Columns)
                {
                    if (column.Visible)
                    {
                        headers.Add(column.HeaderText);
                    }
                }
                sb.AppendLine(string.Join("\t", headers));

                // Add data rows
                foreach (var table in _orchestrator.GetTables())
                {
                    var values = new List<string>();
                    foreach (DataGridViewColumn column in dgvTables.Columns)
                    {
                        if (column.Visible)
                        {
                            string value = column.DataPropertyName switch
                            {
                                "TableName" => table.TableName,
                                "TableId" => table.TableId.ToString(),
                                "StrategyDisplay" => table.StrategyDisplay,
                                "EstimatedSizeMBDisplay" => table.EstimatedSizeMBDisplay,
                                "Tier2RowCountDisplay" => table.Tier2RowCountDisplay,
                                "Tier2SizeGBDisplay" => table.Tier2SizeGBDisplay,
                                "Status" => table.Status.ToString(),
                                "RecordsFetched" => table.RecordsFetched.ToString("N0"),
                                "MinRecId" => table.MinRecId > 0 ? table.MinRecId.ToString("N0") : "",
                                "FetchTimeDisplay" => table.FetchTimeDisplay,
                                "DeleteTimeDisplay" => table.DeleteTimeDisplay,
                                "InsertTimeDisplay" => table.InsertTimeDisplay,
                                "CompareTimeDisplay" => table.CompareTimeDisplay,
                                "UnchangedDisplay" => table.UnchangedDisplay,
                                "ModifiedDisplay" => table.ModifiedDisplay,
                                "NewInTier2Display" => table.NewInTier2Display,
                                "DeletedFromAxDbDisplay" => table.DeletedFromAxDbDisplay,
                                "Error" => table.Error.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Replace("\t", " "),
                                _ => ""
                            };
                            values.Add(value);
                        }
                    }
                    sb.AppendLine(string.Join("\t", values));
                }

                Clipboard.SetText(sb.ToString());
                Log($"Copied {_orchestrator.GetTables().Count} rows to clipboard (tab-delimited format for Excel)");
                MessageBox.Show($"Copied {_orchestrator.GetTables().Count} rows to clipboard.\n\nYou can now paste into Excel.",
                    "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error copying to clipboard: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log($"ERROR copying to clipboard: {ex.Message}");
            }
        }

        private void DgvTables_SelectionChanged(object? sender, EventArgs e)
        {
            UpdateButtonStates();
        }

        private void CopyTableName_Click(object? sender, EventArgs e)
        {
            if (dgvTables.SelectedRows.Count > 0)
            {
                var selectedRow = dgvTables.SelectedRows[0];
                var tableInfo = selectedRow.DataBoundItem as TableInfo;
                if (tableInfo != null && !string.IsNullOrEmpty(tableInfo.TableName))
                {
                    Clipboard.SetText(tableInfo.TableName);
                    Log($"Copied table name to clipboard: {tableInfo.TableName}");
                }
            }
        }

        private void GetSql_Click(object? sender, EventArgs e)
        {
            if (dgvTables.SelectedRows.Count == 0)
            {
                MessageBox.Show("No tables selected", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var sqlBuilder = new System.Text.StringBuilder();

            foreach (DataGridViewRow row in dgvTables.SelectedRows)
            {
                var tableInfo = row.DataBoundItem as TableInfo;
                if (tableInfo == null)
                    continue;

                sqlBuilder.AppendLine(GenerateSqlForTable(tableInfo));
                sqlBuilder.AppendLine();
            }

            string sql = sqlBuilder.ToString();
            Clipboard.SetText(sql);

            UpdateStatus("SQL copied to clipboard");
            Log($"Generated and copied SQL for {dgvTables.SelectedRows.Count} table(s) to clipboard");
        }

        private string GenerateSqlForTable(TableInfo table)
        {
            var sql = new System.Text.StringBuilder();

            // Header
            sql.AppendLine("-- ============================================");
            sql.AppendLine($"-- Table: {table.TableName}");
            sql.AppendLine($"-- Strategy: {table.StrategyDisplay}");
            sql.AppendLine($"-- Cleanup: {GetCleanupDescription(table)}");
            sql.AppendLine("-- ============================================");
            sql.AppendLine();

            // Source Query
            sql.AppendLine("-- === SOURCE QUERY (Tier2) ===");
            sql.AppendLine(table.FetchSql);
            sql.AppendLine();

            // Cleanup Queries
            sql.AppendLine("-- === CLEANUP QUERIES (AxDB) ===");
            sql.AppendLine(GenerateCleanupSql(table));
            sql.AppendLine();

            // Insert
            sql.AppendLine("-- === INSERT ===");
            sql.AppendLine("-- SqlBulkCopy will be used to insert fetched records");
            sql.AppendLine();

            // Sequence Update
            sql.AppendLine("-- === SEQUENCE UPDATE ===");
            sql.AppendLine($"DECLARE @MaxRecId BIGINT = (SELECT MAX(RECID) FROM [{table.TableName}])");
            sql.AppendLine($"DECLARE @TableId INT = {table.TableId} -- Actual TableId from SQLDICTIONARY");
            sql.AppendLine($"IF @MaxRecId > (SELECT CAST(current_value AS BIGINT) FROM sys.sequences WHERE name = 'SEQ_{table.TableId}')");
            sql.AppendLine($"    ALTER SEQUENCE [SEQ_{table.TableId}] RESTART WITH @MaxRecId");

            return sql.ToString();
        }

        private string GetCleanupDescription(TableInfo table)
        {
            // Check for TRUNCATE optimization (same logic as AxDbDataService)
            if (table.Tier2RowCount > 0 &&
                table.RecordsToCopy > 0 &&
                table.Tier2RowCount <= table.RecordsToCopy &&
                !table.UseTruncate)
                return "TRUNCATE (optimization: copying all Tier2 rows)";

            if (table.UseTruncate)
                return "TRUNCATE";

            switch (table.StrategyType)
            {
                case DBSyncTool.Models.CopyStrategyType.RecId:
                case DBSyncTool.Models.CopyStrategyType.Sql:
                    return "Delete by RecId";
                default:
                    return "Unknown";
            }
        }

        private string GenerateCleanupSql(TableInfo table)
        {
            var sql = new System.Text.StringBuilder();

            // Check for TRUNCATE optimization (same logic as AxDbDataService)
            if (table.Tier2RowCount > 0 &&
                table.RecordsToCopy > 0 &&
                table.Tier2RowCount <= table.RecordsToCopy &&
                !table.UseTruncate)
            {
                sql.AppendLine($"-- Optimization: Tier2 has {table.Tier2RowCount} rows, copying {table.RecordsToCopy}");
                sql.AppendLine($"-- Using TRUNCATE instead of DELETE for better performance");
                sql.AppendLine($"TRUNCATE TABLE [{table.TableName}]");
                return sql.ToString();
            }

            if (table.UseTruncate)
            {
                sql.AppendLine($"TRUNCATE TABLE [{table.TableName}]");
                return sql.ToString();
            }

            switch (table.StrategyType)
            {
                case DBSyncTool.Models.CopyStrategyType.RecId:
                case DBSyncTool.Models.CopyStrategyType.Sql:
                    sql.AppendLine($"DELETE FROM [{table.TableName}]");
                    sql.AppendLine("WHERE RECID >= @MinRecId");
                    sql.AppendLine("-- Note: @MinRecId will be determined after fetching source data");
                    break;
            }

            return sql.ToString();
        }

        private void ExitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void OpenConfigFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string configPath = Path.Combine(Application.StartupPath, "Config");
            if (!Directory.Exists(configPath))
            {
                Directory.CreateDirectory(configPath);
            }
            System.Diagnostics.Process.Start("explorer.exe", configPath);
        }

        private async void CheckForUpdatesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Disable menu item during check
            checkForUpdatesToolStripMenuItem.Enabled = false;

            try
            {
                // Update status
                UpdateStatus("Checking for updates...");

                var result = await UpdateChecker.CheckForUpdatesAsync();

                if (!result.Success)
                {
                    // Error occurred
                    ShowUpdateCheckError(result.ErrorMessage ?? "Unknown error");
                    return;
                }

                if (result.UpdateAvailable)
                {
                    // New version available
                    ShowUpdateAvailableDialog(result);
                }
                else
                {
                    // Up to date
                    ShowUpToDateDialog(result);
                }
            }
            finally
            {
                checkForUpdatesToolStripMenuItem.Enabled = true;
                UpdateStatus("Ready");
            }
        }

        private void ShowUpdateAvailableDialog(UpdateChecker.UpdateCheckResult result)
        {
            var message = $"A new version is available!\n\n" +
                          $"Current version:  {result.CurrentVersion}\n" +
                          $"Latest version:   {result.LatestVersion}\n\n" +
                          $"Would you like to open the download page?";

            var dialogResult = MessageBox.Show(
                message,
                "Update Available",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (dialogResult == DialogResult.Yes)
            {
                // Open release page in default browser
                var url = result.ReleaseUrl ?? UpdateChecker.GetReleasesPageUrl();
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not open browser: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ShowUpToDateDialog(UpdateChecker.UpdateCheckResult result)
        {
            MessageBox.Show(
                $"You're up to date!\n\nCurrent version: {result.CurrentVersion}",
                "Check for Updates",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void ShowUpdateCheckError(string errorMessage)
        {
            var dialogResult = MessageBox.Show(
                $"Could not check for updates.\n\n{errorMessage}\n\nWould you like to open the releases page manually?",
                "Check for Updates",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (dialogResult == DialogResult.Yes)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = UpdateChecker.GetReleasesPageUrl(),
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not open browser: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void AboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var versionString = version != null ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}" : "1.0.0.0";

            using var aboutForm = new Form
            {
                Text = "About",
                Width = 400,
                Height = 250,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var lblTitle = new Label
            {
                Text = "D365FO Database Sync Tool",
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                Left = 20,
                Top = 20,
                Width = 350,
                TextAlign = ContentAlignment.MiddleCenter
            };

            var lblVersion = new Label
            {
                Text = "Version:",
                Left = 20,
                Top = 60,
                Width = 60
            };

            var txtVersion = new TextBox
            {
                Text = versionString,
                Left = 85,
                Top = 57,
                Width = 280,
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle
            };

            var lblCopyright = new Label
            {
                Text = $"Copyright © {DateTime.Now.Year} Denis Trunin",
                Left = 20,
                Top = 95,
                Width = 350,
                TextAlign = ContentAlignment.MiddleCenter
            };

            var linkGitHub = new LinkLabel
            {
                Text = "https://github.com/TrudAX/D365FO-DB-Sync",
                Left = 20,
                Top = 125,
                Width = 350,
                TextAlign = ContentAlignment.MiddleCenter
            };
            linkGitHub.LinkClicked += (s, ev) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "https://github.com/TrudAX/D365FO-DB-Sync",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not open link: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            var btnOK = new Button
            {
                Text = "OK",
                Left = 150,
                Top = 160,
                Width = 100,
                DialogResult = DialogResult.OK
            };

            aboutForm.Controls.Add(lblTitle);
            aboutForm.Controls.Add(lblVersion);
            aboutForm.Controls.Add(txtVersion);
            aboutForm.Controls.Add(lblCopyright);
            aboutForm.Controls.Add(linkGitHub);
            aboutForm.Controls.Add(btnOK);
            aboutForm.AcceptButton = btnOK;

            aboutForm.ShowDialog(this);
        }

        private void UnsubscribeOrchestratorEvents()
        {
            if (_orchestrator != null)
            {
                _orchestrator.TablesUpdated -= Orchestrator_TablesUpdated;
                _orchestrator.StatusUpdated -= Orchestrator_StatusUpdated;
                _orchestrator.TimestampsUpdated -= Orchestrator_TimestampsUpdated;
            }
        }

        private void Orchestrator_TablesUpdated(object? sender, List<TableInfo> tables)
        {
            // Don't update immediately, let the timer handle it
        }

        private void Orchestrator_StatusUpdated(object? sender, string status)
        {
            UpdateStatus(status);
        }

        private void Orchestrator_TimestampsUpdated(object? sender, EventArgs e)
        {
            // Track that timestamps were updated during this execution
            _timestampsUpdatedDuringExecution = true;

            // Save configuration immediately when timestamps are updated
            if (!string.IsNullOrWhiteSpace(_currentConfig.ConfigName))
            {
                try
                {
                    _configManager.SaveConfiguration(_currentConfig);
                    // Don't log here to avoid spamming - only log at the end in ExecuteOperationAsync
                }
                catch (Exception ex)
                {
                    Log($"Warning: Could not auto-save configuration: {ex.Message}");
                }
            }
        }

        private void UpdateTimer_Tick(object? sender, EventArgs e)
        {
            if (_orchestrator != null)
            {
                UpdateTablesGrid(_orchestrator.GetTables());
            }
        }

        private async Task ExecuteOperationAsync(Func<Task> operation)
        {
            try
            {
                _isExecuting = true;
                _timestampsUpdatedDuringExecution = false;  // Reset flag before execution
                UpdateButtonStates();
                _updateTimer.Start();

                await Task.Run(operation);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log($"ERROR: {ex.Message}");
            }
            finally
            {
                _isExecuting = false;
                _updateTimer.Stop();
                UpdateButtonStates();

                // Final update
                if (_orchestrator != null)
                {
                    UpdateTablesGrid(_orchestrator.GetTables());

                    // Clear memory from completed tables
                    _orchestrator.ClearCompletedTablesMemory();
                }

                // Refresh timestamp UI (they may have been updated during processing)
                RefreshTimestampUI();

                // Auto-save configuration only if timestamps were actually updated
                if (_timestampsUpdatedDuringExecution && !string.IsNullOrWhiteSpace(_currentConfig.ConfigName))
                {
                    try
                    {
                        _configManager.SaveConfiguration(_currentConfig);
                        Log($"Configuration auto-saved (timestamps updated)");
                    }
                    catch (Exception ex)
                    {
                        Log($"Warning: Could not auto-save configuration: {ex.Message}");
                    }
                }
            }
        }

        private void InitializeSystemExcludedTables()
        {
            string defaultSystemExclusions = string.Join("\r\n", new[]
            {
                "SQL*",
                "UserInfo",
                "Sys*",
                "Batch*",
                "RetailCDX*",
                "RETAILHARDWAREPROFILE",
                "AIFCHANGETRACKINGDELETEDOBJECT",
                "LICENSINGUSEREFFECTIVEROLES",
                "TIMEZONEINFO",
                "FORMRUN*",
                "BUSINESSEVENTSTABLE",
                "FORMCONTROL*",
                "VENDACCOUNTNUMOBJECTREFERENCES",
                "AxKPI*"
            });

            txtSystemExcludedTables.Text = defaultSystemExclusions;
            _currentConfig.SystemExcludedTables = defaultSystemExclusions;
        }

        private void BtnInitSystemExcludedTables_Click(object sender, EventArgs e)
        {
            InitializeSystemExcludedTables();
            Log("System excluded tables initialized with default values");
        }

        private void BtnClearTimestamps_Click(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "This will clear all stored timestamps and disable optimization until the next successful sync.\n\n" +
                "Are you sure you want to continue?",
                "Clear Timestamps",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                txtTier2Timestamps.Text = string.Empty;
                txtAxDBTimestamps.Text = string.Empty;
                _currentConfig.Tier2Timestamps = string.Empty;
                _currentConfig.AxDBTimestamps = string.Empty;
                Log("All timestamps cleared - optimization will be disabled until next successful sync");
            }
        }

        private string? PromptForConfigName(string prompt, string defaultValue)
        {
            using var form = new Form
            {
                Text = "Configuration Name",
                Width = 400,
                Height = 150,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var label = new Label
            {
                Text = prompt,
                Left = 20,
                Top = 20,
                Width = 350
            };

            var textBox = new TextBox
            {
                Text = defaultValue,
                Left = 20,
                Top = 45,
                Width = 340
            };

            var btnOK = new Button
            {
                Text = "OK",
                Left = 200,
                Top = 75,
                DialogResult = DialogResult.OK
            };

            var btnCancel = new Button
            {
                Text = "Cancel",
                Left = 285,
                Top = 75,
                DialogResult = DialogResult.Cancel
            };

            form.Controls.Add(label);
            form.Controls.Add(textBox);
            form.Controls.Add(btnOK);
            form.Controls.Add(btnCancel);
            form.AcceptButton = btnOK;
            form.CancelButton = btnCancel;

            return form.ShowDialog() == DialogResult.OK ? textBox.Text : null;
        }

        private ListSortDirection _lastSortDirection = ListSortDirection.Ascending;
        private string _lastSortColumn = "TableName";

        private void DgvTables_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex < 0 || _tablesBindingList.Count == 0)
                return;

            var column = dgvTables.Columns[e.ColumnIndex];
            var columnName = column.DataPropertyName;

            if (string.IsNullOrEmpty(columnName))
                return;

            // Toggle sort direction if clicking same column, otherwise use ascending
            if (_lastSortColumn == columnName)
            {
                _lastSortDirection = _lastSortDirection == ListSortDirection.Ascending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;
            }
            else
            {
                _lastSortDirection = ListSortDirection.Ascending;
                _lastSortColumn = columnName;
            }

            // Sort the binding list
            SortBindingList(columnName, _lastSortDirection);

            // Update column header to show sort indicator
            foreach (DataGridViewColumn col in dgvTables.Columns)
            {
                col.HeaderCell.SortGlyphDirection = SortOrder.None;
            }

            column.HeaderCell.SortGlyphDirection = _lastSortDirection == ListSortDirection.Ascending
                ? SortOrder.Ascending
                : SortOrder.Descending;
        }

        private void SortBindingList(string propertyName, ListSortDirection direction)
        {
            var items = _tablesBindingList.ToList();

            // Sort based on property name
            IOrderedEnumerable<TableInfo> sortedItems;

            switch (propertyName)
            {
                case "TableName":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.TableName)
                        : items.OrderByDescending(x => x.TableName);
                    break;
                case "TableId":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.TableId)
                        : items.OrderByDescending(x => x.TableId);
                    break;
                case "StrategyDisplay":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.StrategyDisplay)
                        : items.OrderByDescending(x => x.StrategyDisplay);
                    break;
                case "EstimatedSizeMBDisplay":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.EstimatedSizeMB)
                        : items.OrderByDescending(x => x.EstimatedSizeMB);
                    break;
                case "Tier2RowCountDisplay":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.Tier2RowCount)
                        : items.OrderByDescending(x => x.Tier2RowCount);
                    break;
                case "Tier2SizeGBDisplay":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.Tier2SizeGB)
                        : items.OrderByDescending(x => x.Tier2SizeGB);
                    break;
                case "Status":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.Status)
                        : items.OrderByDescending(x => x.Status);
                    break;
                case "RecordsFetched":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.RecordsFetched)
                        : items.OrderByDescending(x => x.RecordsFetched);
                    break;
                case "MinRecId":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.MinRecId)
                        : items.OrderByDescending(x => x.MinRecId);
                    break;
                case "FetchTimeDisplay":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.FetchTimeSeconds)
                        : items.OrderByDescending(x => x.FetchTimeSeconds);
                    break;
                case "DeleteTimeDisplay":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.DeleteTimeSeconds)
                        : items.OrderByDescending(x => x.DeleteTimeSeconds);
                    break;
                case "InsertTimeDisplay":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.InsertTimeSeconds)
                        : items.OrderByDescending(x => x.InsertTimeSeconds);
                    break;
                case "CompareTimeDisplay":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.CompareTimeSeconds)
                        : items.OrderByDescending(x => x.CompareTimeSeconds);
                    break;
                case "UnchangedDisplay":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.UnchangedCount)
                        : items.OrderByDescending(x => x.UnchangedCount);
                    break;
                case "ModifiedDisplay":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.ModifiedCount)
                        : items.OrderByDescending(x => x.ModifiedCount);
                    break;
                case "NewInTier2Display":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.NewInTier2Count)
                        : items.OrderByDescending(x => x.NewInTier2Count);
                    break;
                case "DeletedFromAxDbDisplay":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.DeletedFromAxDbCount)
                        : items.OrderByDescending(x => x.DeletedFromAxDbCount);
                    break;
                case "Error":
                    sortedItems = direction == ListSortDirection.Ascending
                        ? items.OrderBy(x => x.Error)
                        : items.OrderByDescending(x => x.Error);
                    break;
                default:
                    return; // Unknown property, don't sort
            }

            // Clear and refill the binding list
            _tablesBindingList.RaiseListChangedEvents = false;
            _tablesBindingList.Clear();

            foreach (var item in sortedItems)
            {
                _tablesBindingList.Add(item);
            }

            _tablesBindingList.RaiseListChangedEvents = true;
            _tablesBindingList.ResetBindings();
        }
    }
}
