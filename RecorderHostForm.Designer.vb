<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class RecorderHostForm
    Inherits System.Windows.Forms.Form

    <System.Diagnostics.DebuggerNonUserCode()>
    Protected Overrides Sub Dispose(disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    Private components As System.ComponentModel.IContainer
    Private mainLayout As TableLayoutPanel
    Private commonGroupBox As GroupBox
    Private commonPanel As TableLayoutPanel
    Private commonTitleLabel As Label
    Private profileLabel As Label
    Private profileComboBox As ComboBox
    Private intervalLabel As Label
    Private intervalUpDown As NumericUpDown
    Private recordAllButton As Button
    Private stopAllButton As Button
    Private openRecordingsButton As Button
    Private deleteAllButton As Button
    Private darkModeCheckBox As CheckBox
    Private audioListenPanel As FlowLayoutPanel
    Private audioListenLabel As Label
    Private audioListenComboBox As ComboBox
    Private cam1CpuLabel As Label
    Private cam1CpuValueLabel As Label
    Private cam2CpuLabel As Label
    Private cam2CpuValueLabel As Label
    Private cam3CpuLabel As Label
    Private cam3CpuValueLabel As Label
    Private cam4CpuLabel As Label
    Private cam4CpuValueLabel As Label
    Private totalCpuLabel As Label
    Private totalCpuValueLabel As Label
    Private contentLayout As TableLayoutPanel
    Private cameraGrid As TableLayoutPanel
    Private streamGroupBox As GroupBox
    Private streamRecorderControl As StreamRecorderControl
    Private cam1GroupBox As GroupBox
    Private cam2GroupBox As GroupBox
    Private cam3GroupBox As GroupBox
    Private cam4GroupBox As GroupBox
    Private leftRecorderControl As RecorderControl
    Private rightRecorderControl As RecorderControl
    Private thirdRecorderControl As RecorderControl
    Private fourthRecorderControl As RecorderControl

    <System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        components = New System.ComponentModel.Container()
        mainLayout = New TableLayoutPanel()
        commonGroupBox = New GroupBox()
        commonPanel = New TableLayoutPanel()
        commonTitleLabel = New Label()
        profileLabel = New Label()
        profileComboBox = New ComboBox()
        intervalLabel = New Label()
        intervalUpDown = New NumericUpDown()
        recordAllButton = New Button()
        stopAllButton = New Button()
        openRecordingsButton = New Button()
        deleteAllButton = New Button()
        darkModeCheckBox = New CheckBox()
        audioListenPanel = New FlowLayoutPanel()
        audioListenLabel = New Label()
        audioListenComboBox = New ComboBox()
        cam1CpuLabel = New Label()
        cam1CpuValueLabel = New Label()
        cam2CpuLabel = New Label()
        cam2CpuValueLabel = New Label()
        cam3CpuLabel = New Label()
        cam3CpuValueLabel = New Label()
        cam4CpuLabel = New Label()
        cam4CpuValueLabel = New Label()
        totalCpuLabel = New Label()
        totalCpuValueLabel = New Label()
        contentLayout = New TableLayoutPanel()
        cameraGrid = New TableLayoutPanel()
        streamGroupBox = New GroupBox()
        streamRecorderControl = New StreamRecorderControl()
        cam1GroupBox = New GroupBox()
        leftRecorderControl = New RecorderControl()
        cam2GroupBox = New GroupBox()
        rightRecorderControl = New RecorderControl()
        cam3GroupBox = New GroupBox()
        thirdRecorderControl = New RecorderControl()
        cam4GroupBox = New GroupBox()
        fourthRecorderControl = New RecorderControl()
        CType(intervalUpDown, System.ComponentModel.ISupportInitialize).BeginInit()
        mainLayout.SuspendLayout()
        commonGroupBox.SuspendLayout()
        commonPanel.SuspendLayout()
        contentLayout.SuspendLayout()
        cameraGrid.SuspendLayout()
        streamGroupBox.SuspendLayout()
        cam1GroupBox.SuspendLayout()
        cam2GroupBox.SuspendLayout()
        cam3GroupBox.SuspendLayout()
        cam4GroupBox.SuspendLayout()
        SuspendLayout()
        AutoScaleMode = AutoScaleMode.Font
        ClientSize = New Size(1480, 1020)
        FormBorderStyle = FormBorderStyle.Sizable
        MaximizeBox = True
        MinimizeBox = False
        Name = "RecorderHostForm"
        StartPosition = FormStartPosition.CenterScreen
        Text = "4 Channel DeckLink Recorder"
        mainLayout.ColumnCount = 1
        mainLayout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        mainLayout.Controls.Add(commonGroupBox, 0, 0)
        mainLayout.Controls.Add(contentLayout, 0, 1)
        mainLayout.Dock = DockStyle.Fill
        mainLayout.Location = New Point(0, 0)
        mainLayout.Margin = New Padding(0)
        mainLayout.Name = "mainLayout"
        mainLayout.RowCount = 2
        mainLayout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        mainLayout.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        mainLayout.Size = New Size(1480, 1020)
        commonGroupBox.Controls.Add(commonPanel)
        commonGroupBox.Dock = DockStyle.Fill
        commonGroupBox.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        commonGroupBox.Location = New Point(8, 8)
        commonGroupBox.Margin = New Padding(8, 8, 8, 6)
        commonGroupBox.Name = "commonGroupBox"
        commonGroupBox.Padding = New Padding(8, 6, 8, 8)
        commonGroupBox.Size = New Size(1464, 126)
        commonGroupBox.TabStop = False
        commonGroupBox.Text = "COMMON"
        commonPanel.ColumnCount = 2
        commonPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        commonPanel.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        commonPanel.Dock = DockStyle.Fill
        commonPanel.Location = New Point(8, 22)
        commonPanel.Margin = New Padding(0)
        commonPanel.Name = "commonPanel"
        commonPanel.Padding = New Padding(6, 4, 6, 2)
        commonPanel.RowCount = 1
        commonPanel.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        commonPanel.Size = New Size(1448, 96)
        commonTitleLabel.AutoSize = True
        commonTitleLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        commonTitleLabel.Location = New Point(6, 7)
        commonTitleLabel.Margin = New Padding(0, 3, 16, 0)
        commonTitleLabel.Name = "commonTitleLabel"
        commonTitleLabel.Size = New Size(60, 15)
        commonTitleLabel.Text = "Controls:"
        profileLabel.AutoSize = True
        profileLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        profileLabel.Location = New Point(82, 7)
        profileLabel.Margin = New Padding(0, 3, 6, 0)
        profileLabel.Name = "profileLabel"
        profileLabel.Size = New Size(39, 15)
        profileLabel.Text = "Profile"
        profileComboBox.DropDownStyle = ComboBoxStyle.DropDownList
        profileComboBox.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        profileComboBox.FormattingEnabled = True
        profileComboBox.Location = New Point(127, 4)
        profileComboBox.Margin = New Padding(0, 0, 18, 0)
        profileComboBox.Name = "profileComboBox"
        profileComboBox.Size = New Size(176, 23)
        intervalLabel.AutoSize = True
        intervalLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        intervalLabel.Location = New Point(321, 7)
        intervalLabel.Margin = New Padding(0, 3, 6, 0)
        intervalLabel.Name = "intervalLabel"
        intervalLabel.Size = New Size(61, 15)
        intervalLabel.Text = "Interval (s)"
        intervalUpDown.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        intervalUpDown.Location = New Point(388, 4)
        intervalUpDown.Margin = New Padding(0, 0, 18, 0)
        intervalUpDown.Maximum = New Decimal(New Integer() {3600, 0, 0, 0})
        intervalUpDown.Minimum = New Decimal(New Integer() {1, 0, 0, 0})
        intervalUpDown.Name = "intervalUpDown"
        intervalUpDown.Size = New Size(62, 23)
        intervalUpDown.Value = New Decimal(New Integer() {10, 0, 0, 0})
        recordAllButton.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        recordAllButton.Location = New Point(468, 4)
        recordAllButton.Margin = New Padding(0, 0, 8, 0)
        recordAllButton.Name = "recordAllButton"
        recordAllButton.Size = New Size(84, 24)
        recordAllButton.Text = "Record All"
        recordAllButton.UseVisualStyleBackColor = True
        stopAllButton.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        stopAllButton.Location = New Point(560, 4)
        stopAllButton.Margin = New Padding(0, 0, 8, 0)
        stopAllButton.Name = "stopAllButton"
        stopAllButton.Size = New Size(72, 24)
        stopAllButton.Text = "Stop All"
        stopAllButton.UseVisualStyleBackColor = True
        openRecordingsButton.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        openRecordingsButton.Location = New Point(640, 4)
        openRecordingsButton.Margin = New Padding(0, 0, 8, 0)
        openRecordingsButton.Name = "openRecordingsButton"
        openRecordingsButton.Size = New Size(112, 24)
        openRecordingsButton.Text = "Open Recordings"
        openRecordingsButton.UseVisualStyleBackColor = True
        deleteAllButton.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        deleteAllButton.Location = New Point(760, 4)
        deleteAllButton.Margin = New Padding(0, 0, 18, 0)
        deleteAllButton.Name = "deleteAllButton"
        deleteAllButton.Size = New Size(76, 24)
        deleteAllButton.Text = "Delete All"
        deleteAllButton.UseVisualStyleBackColor = True
        darkModeCheckBox.AutoSize = True
        darkModeCheckBox.Checked = True
        darkModeCheckBox.CheckState = CheckState.Checked
        darkModeCheckBox.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        darkModeCheckBox.Location = New Point(854, 7)
        darkModeCheckBox.Margin = New Padding(0, 3, 18, 0)
        darkModeCheckBox.Name = "darkModeCheckBox"
        darkModeCheckBox.Size = New Size(82, 19)
        darkModeCheckBox.Text = "Dark Mode"
        darkModeCheckBox.UseVisualStyleBackColor = True
        audioListenPanel.AutoSize = True
        audioListenPanel.Controls.Add(audioListenLabel)
        audioListenPanel.Controls.Add(audioListenComboBox)
        audioListenPanel.FlowDirection = FlowDirection.LeftToRight
        audioListenPanel.Location = New Point(954, 4)
        audioListenPanel.Margin = New Padding(0, 0, 18, 0)
        audioListenPanel.Name = "audioListenPanel"
        audioListenPanel.Size = New Size(189, 23)
        audioListenPanel.WrapContents = False
        audioListenLabel.AutoSize = True
        audioListenLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        audioListenLabel.Location = New Point(0, 3)
        audioListenLabel.Margin = New Padding(0, 3, 6, 0)
        audioListenLabel.Name = "audioListenLabel"
        audioListenLabel.Size = New Size(71, 15)
        audioListenLabel.Text = "Listen Audio"
        audioListenComboBox.DropDownStyle = ComboBoxStyle.DropDownList
        audioListenComboBox.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        audioListenComboBox.FormattingEnabled = True
        audioListenComboBox.Location = New Point(77, 0)
        audioListenComboBox.Margin = New Padding(0)
        audioListenComboBox.Name = "audioListenComboBox"
        audioListenComboBox.Size = New Size(104, 23)
        cam1CpuLabel.AutoSize = True
        cam1CpuLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        cam1CpuLabel.Location = New Point(205, 30)
        cam1CpuLabel.Margin = New Padding(0, 3, 6, 0)
        cam1CpuLabel.Name = "cam1CpuLabel"
        cam1CpuLabel.Size = New Size(62, 15)
        cam1CpuLabel.Text = "CAM1 CPU"
        cam1CpuValueLabel.AutoSize = True
        cam1CpuValueLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        cam1CpuValueLabel.ForeColor = Color.DimGray
        cam1CpuValueLabel.Location = New Point(273, 30)
        cam1CpuValueLabel.Margin = New Padding(0, 3, 18, 0)
        cam1CpuValueLabel.Name = "cam1CpuValueLabel"
        cam1CpuValueLabel.Size = New Size(32, 15)
        cam1CpuValueLabel.Text = "0.0%"
        cam2CpuLabel.AutoSize = True
        cam2CpuLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        cam2CpuLabel.Location = New Point(323, 30)
        cam2CpuLabel.Margin = New Padding(0, 3, 6, 0)
        cam2CpuLabel.Name = "cam2CpuLabel"
        cam2CpuLabel.Size = New Size(62, 15)
        cam2CpuLabel.Text = "CAM2 CPU"
        cam2CpuValueLabel.AutoSize = True
        cam2CpuValueLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        cam2CpuValueLabel.ForeColor = Color.DimGray
        cam2CpuValueLabel.Location = New Point(391, 30)
        cam2CpuValueLabel.Margin = New Padding(0, 3, 18, 0)
        cam2CpuValueLabel.Name = "cam2CpuValueLabel"
        cam2CpuValueLabel.Size = New Size(32, 15)
        cam2CpuValueLabel.Text = "0.0%"
        cam3CpuLabel.AutoSize = True
        cam3CpuLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        cam3CpuLabel.Location = New Point(441, 30)
        cam3CpuLabel.Margin = New Padding(0, 3, 6, 0)
        cam3CpuLabel.Name = "cam3CpuLabel"
        cam3CpuLabel.Size = New Size(62, 15)
        cam3CpuLabel.Text = "CAM3 CPU"
        cam3CpuValueLabel.AutoSize = True
        cam3CpuValueLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        cam3CpuValueLabel.ForeColor = Color.DimGray
        cam3CpuValueLabel.Location = New Point(509, 30)
        cam3CpuValueLabel.Margin = New Padding(0, 3, 18, 0)
        cam3CpuValueLabel.Name = "cam3CpuValueLabel"
        cam3CpuValueLabel.Size = New Size(32, 15)
        cam3CpuValueLabel.Text = "0.0%"
        cam4CpuLabel.AutoSize = True
        cam4CpuLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        cam4CpuLabel.Location = New Point(559, 30)
        cam4CpuLabel.Margin = New Padding(0, 3, 6, 0)
        cam4CpuLabel.Name = "cam4CpuLabel"
        cam4CpuLabel.Size = New Size(62, 15)
        cam4CpuLabel.Text = "CAM4 CPU"
        cam4CpuValueLabel.AutoSize = True
        cam4CpuValueLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        cam4CpuValueLabel.ForeColor = Color.DimGray
        cam4CpuValueLabel.Location = New Point(627, 30)
        cam4CpuValueLabel.Margin = New Padding(0, 3, 18, 0)
        cam4CpuValueLabel.Name = "cam4CpuValueLabel"
        cam4CpuValueLabel.Size = New Size(32, 15)
        cam4CpuValueLabel.Text = "0.0%"
        totalCpuLabel.AutoSize = True
        totalCpuLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        totalCpuLabel.Location = New Point(677, 30)
        totalCpuLabel.Margin = New Padding(0, 3, 6, 0)
        totalCpuLabel.Name = "totalCpuLabel"
        totalCpuLabel.Size = New Size(45, 15)
        totalCpuLabel.Text = "PC CPU"
        totalCpuValueLabel.AutoSize = True
        totalCpuValueLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        totalCpuValueLabel.ForeColor = Color.DimGray
        totalCpuValueLabel.Location = New Point(728, 30)
        totalCpuValueLabel.Margin = New Padding(0, 3, 0, 0)
        totalCpuValueLabel.Name = "totalCpuValueLabel"
        totalCpuValueLabel.Size = New Size(32, 15)
        totalCpuValueLabel.Text = "0.0%"
        contentLayout.ColumnCount = 2
        contentLayout.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 1120.0F))
        contentLayout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        contentLayout.Controls.Add(cameraGrid, 0, 0)
        contentLayout.Controls.Add(streamGroupBox, 1, 0)
        contentLayout.Dock = DockStyle.Fill
        contentLayout.Location = New Point(0, 140)
        contentLayout.Margin = New Padding(0)
        contentLayout.Name = "contentLayout"
        contentLayout.RowCount = 1
        contentLayout.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        contentLayout.Size = New Size(1480, 880)
        cameraGrid.ColumnCount = 2
        cameraGrid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
        cameraGrid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
        cameraGrid.Controls.Add(cam1GroupBox, 0, 0)
        cameraGrid.Controls.Add(cam2GroupBox, 1, 0)
        cameraGrid.Controls.Add(cam3GroupBox, 0, 1)
        cameraGrid.Controls.Add(cam4GroupBox, 1, 1)
        cameraGrid.Dock = DockStyle.Fill
        cameraGrid.Location = New Point(0, 0)
        cameraGrid.Margin = New Padding(0)
        cameraGrid.Name = "cameraGrid"
        cameraGrid.Padding = New Padding(10, 0, 10, 10)
        cameraGrid.RowCount = 2
        cameraGrid.RowStyles.Add(New RowStyle(SizeType.Percent, 50.0F))
        cameraGrid.RowStyles.Add(New RowStyle(SizeType.Percent, 50.0F))
        cameraGrid.Size = New Size(1120, 880)
        streamGroupBox.Controls.Add(streamRecorderControl)
        streamGroupBox.Dock = DockStyle.Fill
        streamGroupBox.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        streamGroupBox.Location = New Point(1128, 8)
        streamGroupBox.Margin = New Padding(8, 8, 10, 10)
        streamGroupBox.Name = "streamGroupBox"
        streamGroupBox.Padding = New Padding(8, 6, 8, 8)
        streamGroupBox.Size = New Size(342, 862)
        streamGroupBox.TabStop = False
        streamGroupBox.Text = "STREAM / URL"
        streamRecorderControl.Dock = DockStyle.Fill
        streamRecorderControl.Location = New Point(8, 22)
        streamRecorderControl.Margin = New Padding(0)
        streamRecorderControl.Name = "streamRecorderControl"
        streamRecorderControl.Size = New Size(326, 832)
        cam1GroupBox.Controls.Add(leftRecorderControl)
        cam1GroupBox.Dock = DockStyle.Fill
        cam1GroupBox.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        cam1GroupBox.Location = New Point(18, 8)
        cam1GroupBox.Margin = New Padding(8)
        cam1GroupBox.Name = "cam1GroupBox"
        cam1GroupBox.Padding = New Padding(8, 6, 8, 8)
        cam1GroupBox.Size = New Size(542, 416)
        cam1GroupBox.TabStop = False
        cam1GroupBox.Text = "CAM1"
        leftRecorderControl.CameraName = "CAM1"
        leftRecorderControl.Dock = DockStyle.Fill
        leftRecorderControl.Location = New Point(8, 22)
        leftRecorderControl.Margin = New Padding(0)
        leftRecorderControl.Name = "leftRecorderControl"
        leftRecorderControl.SettingsKey = "CAM1"
        leftRecorderControl.Size = New Size(526, 386)
        cam2GroupBox.Controls.Add(rightRecorderControl)
        cam2GroupBox.Dock = DockStyle.Fill
        cam2GroupBox.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        cam2GroupBox.Location = New Point(560, 8)
        cam2GroupBox.Margin = New Padding(8)
        cam2GroupBox.Name = "cam2GroupBox"
        cam2GroupBox.Padding = New Padding(8, 6, 8, 8)
        cam2GroupBox.Size = New Size(542, 416)
        cam2GroupBox.TabStop = False
        cam2GroupBox.Text = "CAM2"
        rightRecorderControl.CameraName = "CAM2"
        rightRecorderControl.Dock = DockStyle.Fill
        rightRecorderControl.Location = New Point(8, 22)
        rightRecorderControl.Margin = New Padding(0)
        rightRecorderControl.Name = "rightRecorderControl"
        rightRecorderControl.SettingsKey = "CAM2"
        rightRecorderControl.Size = New Size(526, 386)
        cam3GroupBox.Controls.Add(thirdRecorderControl)
        cam3GroupBox.Dock = DockStyle.Fill
        cam3GroupBox.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        cam3GroupBox.Location = New Point(18, 408)
        cam3GroupBox.Margin = New Padding(8)
        cam3GroupBox.Name = "cam3GroupBox"
        cam3GroupBox.Padding = New Padding(8, 6, 8, 8)
        cam3GroupBox.Size = New Size(542, 421)
        cam3GroupBox.TabStop = False
        cam3GroupBox.Text = "CAM3"
        thirdRecorderControl.CameraName = "CAM3"
        thirdRecorderControl.Dock = DockStyle.Fill
        thirdRecorderControl.Location = New Point(8, 22)
        thirdRecorderControl.Margin = New Padding(0)
        thirdRecorderControl.Name = "thirdRecorderControl"
        thirdRecorderControl.SettingsKey = "CAM3"
        thirdRecorderControl.Size = New Size(526, 391)
        cam4GroupBox.Controls.Add(fourthRecorderControl)
        cam4GroupBox.Dock = DockStyle.Fill
        cam4GroupBox.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        cam4GroupBox.Location = New Point(560, 408)
        cam4GroupBox.Margin = New Padding(8)
        cam4GroupBox.Name = "cam4GroupBox"
        cam4GroupBox.Padding = New Padding(8, 6, 8, 8)
        cam4GroupBox.Size = New Size(542, 421)
        cam4GroupBox.TabStop = False
        cam4GroupBox.Text = "CAM4"
        fourthRecorderControl.CameraName = "CAM4"
        fourthRecorderControl.Dock = DockStyle.Fill
        fourthRecorderControl.Location = New Point(8, 22)
        fourthRecorderControl.Margin = New Padding(0)
        fourthRecorderControl.Name = "fourthRecorderControl"
        fourthRecorderControl.SettingsKey = "CAM4"
        fourthRecorderControl.Size = New Size(526, 391)
        Controls.Add(mainLayout)
        CType(intervalUpDown, System.ComponentModel.ISupportInitialize).EndInit()
        mainLayout.ResumeLayout(False)
        mainLayout.PerformLayout()
        commonGroupBox.ResumeLayout(False)
        commonGroupBox.PerformLayout()
        commonPanel.ResumeLayout(False)
        commonPanel.PerformLayout()
        contentLayout.ResumeLayout(False)
        cameraGrid.ResumeLayout(False)
        streamGroupBox.ResumeLayout(False)
        cam1GroupBox.ResumeLayout(False)
        cam2GroupBox.ResumeLayout(False)
        cam3GroupBox.ResumeLayout(False)
        cam4GroupBox.ResumeLayout(False)
        ResumeLayout(False)
    End Sub
End Class
