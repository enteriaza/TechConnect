// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    MainForm.Designer.cs
//
// Purpose:
// WinForms designer-generated layout for MainForm: toolbar, status indicators,
// traffic stats, error line, and VT100 serial console panel.
//
// -----------------------------------------------------------------------------

#nullable disable
namespace TechConnect.Client.Forms;

/// <summary>
/// Designer partial of <see cref="MainForm"/> containing control fields and layout.
/// </summary>
partial class MainForm
{
    /// <summary>
    /// Designer component container; disposed when <see cref="Dispose(bool)"/> runs with <c>disposing</c> true.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>Caption for the serial-port combo box.</summary>
    private Label _serialPortLabel;

    /// <summary>Drop-down list of discovered serial ports.</summary>
    private ComboBox _serialPortComboBox;

    /// <summary>Triggers a manual serial-port list refresh.</summary>
    private Button _refreshPortsButton;

    /// <summary>Toggles the physical serial session (Connect / Disconnect).</summary>
    private Button _connectButton;

    /// <summary>Clears the on-screen console buffer and terminal display.</summary>
    private Button _clearConsoleButton;

    /// <summary>Runs the serial loopback diagnostic against the open COM port.</summary>
    private Button _loopbackTestButton;

    /// <summary>Static "Serial:" caption in the status row.</summary>
    private Label _serialStatusCaptionLabel;

    /// <summary>Live physical serial link status value.</summary>
    private Label _serialStatusValueLabel;

    /// <summary>Static "Remote:" caption in the status row.</summary>
    private Label _remoteStatusCaptionLabel;

    /// <summary>Live remote-session status value.</summary>
    private Label _remoteStatusValueLabel;

    /// <summary>Static "NOC:" caption in the status row.</summary>
    private Label _nocStatusCaptionLabel;

    /// <summary>Live NOC attachment status value.</summary>
    private Label _nocStatusValueLabel;

    /// <summary>Static "Selected port:" caption in the status row.</summary>
    private Label _selectedPortCaptionLabel;

    /// <summary>Display name of the currently selected or connected port.</summary>
    private Label _selectedPortValueLabel;

    /// <summary>Static "TX" caption beside the transmit activity indicator.</summary>
    private System.Windows.Forms.Label _txCaptionLabel;

    /// <summary>Color block that blinks on serial transmit activity.</summary>
    private System.Windows.Forms.Label _txIndicatorLabel;

    /// <summary>Static "RX" caption beside the receive activity indicator.</summary>
    private System.Windows.Forms.Label _rxCaptionLabel;

    /// <summary>Color block that blinks on serial receive activity.</summary>
    private System.Windows.Forms.Label _rxIndicatorLabel;

    /// <summary>Byte counters for local TX/RX and remote delivery queue/drop stats.</summary>
    private Label _statsLabel;

    /// <summary>Operator-facing status or error line above the console.</summary>
    private System.Windows.Forms.Label _errorLabel;

    /// <summary>VT100/ANSI terminal panel showing local serial RX.</summary>
    private SerialTerminalControl _consoleTerminal;

    /// <summary>Root fill layout hosting toolbar, status, indicators, error, and console rows.</summary>
    private TableLayoutPanel _rootLayout;

    /// <summary>Top toolbar row: port selector and action buttons.</summary>
    private TableLayoutPanel _toolbarLayout;

    /// <summary>Flow row of serial/remote/NOC/selected-port status labels.</summary>
    private FlowLayoutPanel _statusFlow;

    /// <summary>Flow row of TX/RX indicators and traffic statistics.</summary>
    private System.Windows.Forms.FlowLayoutPanel _indicatorsFlow;

    /// <summary>
    /// Releases designer-owned components when disposing.
    /// </summary>
    /// <param name="disposing">
    /// <see langword="true"/> to dispose managed components; <see langword="false"/> for finalizer path.
    /// </param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Required method for Designer support - do not modify
    /// the contents of this method with the code editor.
    /// </summary>
    /// <remarks>
    /// Instantiates and lays out all controls declared above. Event wiring for buttons
    /// points at handlers defined in the non-designer <see cref="MainForm"/> partial.
    /// </remarks>
    private void InitializeComponent()
    {
        _rootLayout = new System.Windows.Forms.TableLayoutPanel();
        _toolbarLayout = new System.Windows.Forms.TableLayoutPanel();
        _serialPortLabel = new System.Windows.Forms.Label();
        _serialPortComboBox = new System.Windows.Forms.ComboBox();
        _refreshPortsButton = new System.Windows.Forms.Button();
        _connectButton = new System.Windows.Forms.Button();
        _clearConsoleButton = new System.Windows.Forms.Button();
        _loopbackTestButton = new System.Windows.Forms.Button();
        _statusFlow = new System.Windows.Forms.FlowLayoutPanel();
        _serialStatusCaptionLabel = new System.Windows.Forms.Label();
        _serialStatusValueLabel = new System.Windows.Forms.Label();
        _remoteStatusCaptionLabel = new System.Windows.Forms.Label();
        _remoteStatusValueLabel = new System.Windows.Forms.Label();
        _nocStatusCaptionLabel = new System.Windows.Forms.Label();
        _nocStatusValueLabel = new System.Windows.Forms.Label();
        _selectedPortCaptionLabel = new System.Windows.Forms.Label();
        _selectedPortValueLabel = new System.Windows.Forms.Label();
        _indicatorsFlow = new System.Windows.Forms.FlowLayoutPanel();
        _txCaptionLabel = new System.Windows.Forms.Label();
        _txIndicatorLabel = new System.Windows.Forms.Label();
        _rxCaptionLabel = new System.Windows.Forms.Label();
        _rxIndicatorLabel = new System.Windows.Forms.Label();
        _statsLabel = new System.Windows.Forms.Label();
        _errorLabel = new System.Windows.Forms.Label();
        _consoleTerminal = new SerialTerminalControl();
        _rootLayout.SuspendLayout();
        _toolbarLayout.SuspendLayout();
        _statusFlow.SuspendLayout();
        _indicatorsFlow.SuspendLayout();
        SuspendLayout();
        // 
        // _rootLayout
        // 
        _rootLayout.ColumnCount = 1;
        _rootLayout.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
        _rootLayout.Controls.Add(_toolbarLayout, 0, 0);
        _rootLayout.Controls.Add(_statusFlow, 0, 1);
        _rootLayout.Controls.Add(_indicatorsFlow, 0, 2);
        _rootLayout.Controls.Add(_errorLabel, 0, 3);
        _rootLayout.Controls.Add(_consoleTerminal, 0, 4);
        _rootLayout.Dock = System.Windows.Forms.DockStyle.Fill;
        _rootLayout.Location = new System.Drawing.Point(0, 0);
        _rootLayout.Name = "_rootLayout";
        _rootLayout.Padding = new System.Windows.Forms.Padding(12);
        _rootLayout.RowCount = 5;
        _rootLayout.RowStyles.Add(new System.Windows.Forms.RowStyle());
        _rootLayout.RowStyles.Add(new System.Windows.Forms.RowStyle());
        _rootLayout.RowStyles.Add(new System.Windows.Forms.RowStyle());
        _rootLayout.RowStyles.Add(new System.Windows.Forms.RowStyle());
        _rootLayout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
        _rootLayout.Size = new System.Drawing.Size(960, 640);
        _rootLayout.TabIndex = 0;
        // 
        // _toolbarLayout
        // 
        _toolbarLayout.AutoSize = true;
        _toolbarLayout.ColumnCount = 6;
        _toolbarLayout.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
        _toolbarLayout.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
        _toolbarLayout.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
        _toolbarLayout.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
        _toolbarLayout.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
        _toolbarLayout.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
        _toolbarLayout.Controls.Add(_serialPortLabel, 0, 0);
        _toolbarLayout.Controls.Add(_serialPortComboBox, 1, 0);
        _toolbarLayout.Controls.Add(_refreshPortsButton, 2, 0);
        _toolbarLayout.Controls.Add(_connectButton, 3, 0);
        _toolbarLayout.Controls.Add(_clearConsoleButton, 4, 0);
        _toolbarLayout.Controls.Add(_loopbackTestButton, 5, 0);
        _toolbarLayout.Dock = System.Windows.Forms.DockStyle.Fill;
        _toolbarLayout.Location = new System.Drawing.Point(15, 15);
        _toolbarLayout.Name = "_toolbarLayout";
        _toolbarLayout.Padding = new System.Windows.Forms.Padding(0, 0, 0, 8);
        _toolbarLayout.RowCount = 1;
        _toolbarLayout.RowStyles.Add(new System.Windows.Forms.RowStyle());
        _toolbarLayout.Size = new System.Drawing.Size(930, 44);
        _toolbarLayout.TabIndex = 0;
        // 
        // _serialPortLabel
        // 
        _serialPortLabel.Anchor = System.Windows.Forms.AnchorStyles.Left;
        _serialPortLabel.AutoSize = true;
        _serialPortLabel.Location = new System.Drawing.Point(3, 8);
        _serialPortLabel.Name = "_serialPortLabel";
        _serialPortLabel.Size = new System.Drawing.Size(78, 20);
        _serialPortLabel.TabIndex = 0;
        _serialPortLabel.Text = "Serial port";
        _serialPortLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        // 
        // _serialPortComboBox
        // 
        _serialPortComboBox.Anchor = ((System.Windows.Forms.AnchorStyles)(System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right));
        _serialPortComboBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
        _serialPortComboBox.Location = new System.Drawing.Point(87, 4);
        _serialPortComboBox.Name = "_serialPortComboBox";
        _serialPortComboBox.Size = new System.Drawing.Size(446, 28);
        _serialPortComboBox.TabIndex = 1;
        // 
        // _refreshPortsButton
        // 
        _refreshPortsButton.AutoSize = true;
        _refreshPortsButton.Location = new System.Drawing.Point(539, 3);
        _refreshPortsButton.Name = "_refreshPortsButton";
        _refreshPortsButton.Size = new System.Drawing.Size(75, 30);
        _refreshPortsButton.TabIndex = 2;
        _refreshPortsButton.Text = "Refresh";
        _refreshPortsButton.UseVisualStyleBackColor = true;
        _refreshPortsButton.Click += RefreshPortsButton_Click;
        // 
        // _connectButton
        // 
        _connectButton.AutoSize = true;
        _connectButton.Enabled = false;
        _connectButton.Location = new System.Drawing.Point(620, 3);
        _connectButton.Name = "_connectButton";
        _connectButton.Size = new System.Drawing.Size(75, 30);
        _connectButton.TabIndex = 3;
        _connectButton.Text = "Connect";
        _connectButton.UseVisualStyleBackColor = true;
        _connectButton.Click += ConnectButton_Click;
        // 
        // _clearConsoleButton
        // 
        _clearConsoleButton.AutoSize = true;
        _clearConsoleButton.Location = new System.Drawing.Point(701, 3);
        _clearConsoleButton.Name = "_clearConsoleButton";
        _clearConsoleButton.Size = new System.Drawing.Size(108, 30);
        _clearConsoleButton.TabIndex = 4;
        _clearConsoleButton.Text = "Clear console";
        _clearConsoleButton.UseVisualStyleBackColor = true;
        _clearConsoleButton.Click += ClearConsoleButton_Click;
        // 
        // _loopbackTestButton
        // 
        _loopbackTestButton.AutoSize = true;
        _loopbackTestButton.Enabled = false;
        _loopbackTestButton.Location = new System.Drawing.Point(815, 3);
        _loopbackTestButton.Name = "_loopbackTestButton";
        _loopbackTestButton.Size = new System.Drawing.Size(112, 30);
        _loopbackTestButton.TabIndex = 5;
        _loopbackTestButton.Text = "Loopback test";
        _loopbackTestButton.UseVisualStyleBackColor = true;
        _loopbackTestButton.Click += LoopbackTestButton_Click;
        // 
        // _statusFlow
        // 
        _statusFlow.AutoSize = true;
        _statusFlow.Controls.Add(_serialStatusCaptionLabel);
        _statusFlow.Controls.Add(_serialStatusValueLabel);
        _statusFlow.Controls.Add(_remoteStatusCaptionLabel);
        _statusFlow.Controls.Add(_remoteStatusValueLabel);
        _statusFlow.Controls.Add(_nocStatusCaptionLabel);
        _statusFlow.Controls.Add(_nocStatusValueLabel);
        _statusFlow.Controls.Add(_selectedPortCaptionLabel);
        _statusFlow.Controls.Add(_selectedPortValueLabel);
        _statusFlow.Dock = System.Windows.Forms.DockStyle.Fill;
        _statusFlow.Location = new System.Drawing.Point(15, 65);
        _statusFlow.Name = "_statusFlow";
        _statusFlow.Padding = new System.Windows.Forms.Padding(0, 0, 0, 4);
        _statusFlow.Size = new System.Drawing.Size(930, 32);
        _statusFlow.TabIndex = 1;
        // 
        // _serialStatusCaptionLabel
        // 
        _serialStatusCaptionLabel.AutoSize = true;
        _serialStatusCaptionLabel.Location = new System.Drawing.Point(3, 0);
        _serialStatusCaptionLabel.Name = "_serialStatusCaptionLabel";
        _serialStatusCaptionLabel.Padding = new System.Windows.Forms.Padding(0, 4, 4, 4);
        _serialStatusCaptionLabel.Size = new System.Drawing.Size(53, 28);
        _serialStatusCaptionLabel.TabIndex = 0;
        _serialStatusCaptionLabel.Text = "Serial:";
        // 
        // _serialStatusValueLabel
        // 
        _serialStatusValueLabel.AutoSize = true;
        _serialStatusValueLabel.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
        _serialStatusValueLabel.Location = new System.Drawing.Point(62, 0);
        _serialStatusValueLabel.Name = "_serialStatusValueLabel";
        _serialStatusValueLabel.Padding = new System.Windows.Forms.Padding(0, 4, 16, 4);
        _serialStatusValueLabel.Size = new System.Drawing.Size(119, 28);
        _serialStatusValueLabel.TabIndex = 1;
        _serialStatusValueLabel.Text = "Disconnected";
        // 
        // _remoteStatusCaptionLabel
        // 
        _remoteStatusCaptionLabel.AutoSize = true;
        _remoteStatusCaptionLabel.Location = new System.Drawing.Point(187, 0);
        _remoteStatusCaptionLabel.Name = "_remoteStatusCaptionLabel";
        _remoteStatusCaptionLabel.Padding = new System.Windows.Forms.Padding(0, 4, 4, 4);
        _remoteStatusCaptionLabel.Size = new System.Drawing.Size(68, 28);
        _remoteStatusCaptionLabel.TabIndex = 2;
        _remoteStatusCaptionLabel.Text = "Remote:";
        // 
        // _remoteStatusValueLabel
        // 
        _remoteStatusValueLabel.AutoSize = true;
        _remoteStatusValueLabel.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
        _remoteStatusValueLabel.Location = new System.Drawing.Point(261, 0);
        _remoteStatusValueLabel.Name = "_remoteStatusValueLabel";
        _remoteStatusValueLabel.Padding = new System.Windows.Forms.Padding(0, 4, 16, 4);
        _remoteStatusValueLabel.Size = new System.Drawing.Size(119, 28);
        _remoteStatusValueLabel.TabIndex = 3;
        _remoteStatusValueLabel.Text = "Disconnected";
        // 
        // _nocStatusCaptionLabel
        // 
        _nocStatusCaptionLabel.AutoSize = true;
        _nocStatusCaptionLabel.Location = new System.Drawing.Point(386, 0);
        _nocStatusCaptionLabel.Name = "_nocStatusCaptionLabel";
        _nocStatusCaptionLabel.Padding = new System.Windows.Forms.Padding(0, 4, 4, 4);
        _nocStatusCaptionLabel.Size = new System.Drawing.Size(47, 28);
        _nocStatusCaptionLabel.TabIndex = 4;
        _nocStatusCaptionLabel.Text = "NOC:";
        // 
        // _nocStatusValueLabel
        // 
        _nocStatusValueLabel.AutoSize = true;
        _nocStatusValueLabel.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
        _nocStatusValueLabel.Location = new System.Drawing.Point(439, 0);
        _nocStatusValueLabel.Name = "_nocStatusValueLabel";
        _nocStatusValueLabel.Padding = new System.Windows.Forms.Padding(0, 4, 16, 4);
        _nocStatusValueLabel.Size = new System.Drawing.Size(63, 28);
        _nocStatusValueLabel.TabIndex = 5;
        _nocStatusValueLabel.Text = "None";
        // 
        // _selectedPortCaptionLabel
        // 
        _selectedPortCaptionLabel.AutoSize = true;
        _selectedPortCaptionLabel.Location = new System.Drawing.Point(508, 0);
        _selectedPortCaptionLabel.Name = "_selectedPortCaptionLabel";
        _selectedPortCaptionLabel.Padding = new System.Windows.Forms.Padding(0, 4, 4, 4);
        _selectedPortCaptionLabel.Size = new System.Drawing.Size(105, 28);
        _selectedPortCaptionLabel.TabIndex = 6;
        _selectedPortCaptionLabel.Text = "Selected port:";
        // 
        // _selectedPortValueLabel
        // 
        _selectedPortValueLabel.AutoSize = true;
        _selectedPortValueLabel.Location = new System.Drawing.Point(619, 0);
        _selectedPortValueLabel.Name = "_selectedPortValueLabel";
        _selectedPortValueLabel.Padding = new System.Windows.Forms.Padding(0, 4, 0, 4);
        _selectedPortValueLabel.Size = new System.Drawing.Size(52, 28);
        _selectedPortValueLabel.TabIndex = 7;
        _selectedPortValueLabel.Text = "(none)";
        // 
        // _indicatorsFlow
        // 
        _indicatorsFlow.AutoSize = true;
        _indicatorsFlow.Controls.Add(_txCaptionLabel);
        _indicatorsFlow.Controls.Add(_txIndicatorLabel);
        _indicatorsFlow.Controls.Add(_rxCaptionLabel);
        _indicatorsFlow.Controls.Add(_rxIndicatorLabel);
        _indicatorsFlow.Controls.Add(_statsLabel);
        _indicatorsFlow.Dock = System.Windows.Forms.DockStyle.Fill;
        _indicatorsFlow.Location = new System.Drawing.Point(15, 103);
        _indicatorsFlow.Name = "_indicatorsFlow";
        _indicatorsFlow.Padding = new System.Windows.Forms.Padding(0, 0, 0, 8);
        _indicatorsFlow.Size = new System.Drawing.Size(930, 36);
        _indicatorsFlow.TabIndex = 2;
        _indicatorsFlow.WrapContents = false;
        // 
        // _txCaptionLabel
        // 
        _txCaptionLabel.AutoSize = true;
        _txCaptionLabel.Location = new System.Drawing.Point(3, 0);
        _txCaptionLabel.Name = "_txCaptionLabel";
        _txCaptionLabel.Padding = new System.Windows.Forms.Padding(0, 4, 4, 4);
        _txCaptionLabel.Size = new System.Drawing.Size(30, 28);
        _txCaptionLabel.TabIndex = 0;
        _txCaptionLabel.Text = "TX";
        // 
        // _txIndicatorLabel
        // 
        _txIndicatorLabel.BackColor = System.Drawing.Color.DimGray;
        _txIndicatorLabel.Location = new System.Drawing.Point(36, 12);
        _txIndicatorLabel.Margin = new System.Windows.Forms.Padding(0, 12, 10, 0);
        _txIndicatorLabel.Name = "_txIndicatorLabel";
        _txIndicatorLabel.Size = new System.Drawing.Size(14, 7);
        _txIndicatorLabel.TabIndex = 1;
        // 
        // _rxCaptionLabel
        // 
        _rxCaptionLabel.AutoSize = true;
        _rxCaptionLabel.Location = new System.Drawing.Point(63, 0);
        _rxCaptionLabel.Name = "_rxCaptionLabel";
        _rxCaptionLabel.Padding = new System.Windows.Forms.Padding(0, 4, 4, 4);
        _rxCaptionLabel.Size = new System.Drawing.Size(31, 28);
        _rxCaptionLabel.TabIndex = 2;
        _rxCaptionLabel.Text = "RX";
        // 
        // _rxIndicatorLabel
        // 
        _rxIndicatorLabel.BackColor = System.Drawing.Color.DimGray;
        _rxIndicatorLabel.Location = new System.Drawing.Point(97, 12);
        _rxIndicatorLabel.Margin = new System.Windows.Forms.Padding(0, 12, 10, 0);
        _rxIndicatorLabel.Name = "_rxIndicatorLabel";
        _rxIndicatorLabel.Size = new System.Drawing.Size(14, 7);
        _rxIndicatorLabel.TabIndex = 3;
        // 
        // _statsLabel
        // 
        _statsLabel.AutoSize = true;
        _statsLabel.Location = new System.Drawing.Point(124, 0);
        _statsLabel.Name = "_statsLabel";
        _statsLabel.Padding = new System.Windows.Forms.Padding(0, 4, 0, 4);
        _statsLabel.Size = new System.Drawing.Size(172, 28);
        _statsLabel.TabIndex = 4;
        _statsLabel.Text = "TX: 0 bytes  |  RX: 0 bytes";
        // 
        // _errorLabel
        // 
        _errorLabel.AutoSize = true;
        _errorLabel.Dock = System.Windows.Forms.DockStyle.Fill;
        _errorLabel.ForeColor = System.Drawing.Color.Firebrick;
        _errorLabel.Location = new System.Drawing.Point(15, 142);
        _errorLabel.Name = "_errorLabel";
        _errorLabel.Padding = new System.Windows.Forms.Padding(0, 0, 0, 8);
        _errorLabel.Size = new System.Drawing.Size(930, 28);
        _errorLabel.TabIndex = 3;
        _errorLabel.Text = "Select a serial port and click Connect.";
        // 
        // _consoleTerminal
        // 
        _consoleTerminal.BackColor = System.Drawing.Color.Black;
        _consoleTerminal.Dock = System.Windows.Forms.DockStyle.Fill;
        _consoleTerminal.Font = new System.Drawing.Font("Consolas", 10F);
        _consoleTerminal.ForeColor = System.Drawing.Color.LightGray;
        _consoleTerminal.Location = new System.Drawing.Point(15, 173);
        _consoleTerminal.Name = "_consoleTerminal";
        _consoleTerminal.Size = new System.Drawing.Size(930, 452);
        _consoleTerminal.TabIndex = 5;
        _consoleTerminal.TabStop = false;
        // 
        // MainForm
        // 
        AutoScaleDimensions = new System.Drawing.SizeF(8F, 20F);
        AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(960, 640);
        Controls.Add(_rootLayout);
        Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
        MinimumSize = new System.Drawing.Size(720, 480);
        StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
        Text = "TechConnect.Client";
        _rootLayout.ResumeLayout(false);
        _rootLayout.PerformLayout();
        _toolbarLayout.ResumeLayout(false);
        _toolbarLayout.PerformLayout();
        _statusFlow.ResumeLayout(false);
        _statusFlow.PerformLayout();
        _indicatorsFlow.ResumeLayout(false);
        _indicatorsFlow.PerformLayout();
        ResumeLayout(false);
    }
}
