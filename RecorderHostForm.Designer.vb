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
    Private commonPanel As FlowLayoutPanel
    Private commonTitleLabel As Label
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
    Private cameraGrid As TableLayoutPanel
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
        commonPanel = New FlowLayoutPanel()
        commonTitleLabel = New Label()
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
        cameraGrid = New TableLayoutPanel()
        cam1GroupBox = New GroupBox()
        leftRecorderControl = New RecorderControl()
        cam2GroupBox = New GroupBox()
        rightRecorderControl = New RecorderControl()
        cam3GroupBox = New GroupBox()
        thirdRecorderControl = New RecorderControl()
        cam4GroupBox = New GroupBox()
        fourthRecorderControl = New RecorderControl()
        mainLayout.SuspendLayout()
        commonPanel.SuspendLayout()
        cameraGrid.SuspendLayout()
        cam1GroupBox.SuspendLayout()
        cam2GroupBox.SuspendLayout()
        cam3GroupBox.SuspendLayout()
        cam4GroupBox.SuspendLayout()
        SuspendLayout()
        AutoScaleMode = AutoScaleMode.Font
        ClientSize = New Size(980, 888)
        FormBorderStyle = FormBorderStyle.FixedSingle
        MaximizeBox = False
        MinimizeBox = False
        Name = "RecorderHostForm"
        StartPosition = FormStartPosition.CenterScreen
        Text = "DeckLink Recorder"
        mainLayout.ColumnCount = 1
        mainLayout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        mainLayout.Controls.Add(commonPanel, 0, 0)
        mainLayout.Controls.Add(cameraGrid, 0, 1)
        mainLayout.Dock = DockStyle.Fill
        mainLayout.Location = New Point(0, 0)
        mainLayout.Margin = New Padding(0)
        mainLayout.Name = "mainLayout"
        mainLayout.RowCount = 2
        mainLayout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        mainLayout.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        mainLayout.Size = New Size(980, 888)
        commonPanel.AutoSize = True
        commonPanel.Controls.Add(commonTitleLabel)
        commonPanel.Controls.Add(audioListenLabel)
        commonPanel.Controls.Add(audioListenComboBox)
        commonPanel.Controls.Add(cam1CpuLabel)
        commonPanel.Controls.Add(cam1CpuValueLabel)
        commonPanel.Controls.Add(cam2CpuLabel)
        commonPanel.Controls.Add(cam2CpuValueLabel)
        commonPanel.Controls.Add(cam3CpuLabel)
        commonPanel.Controls.Add(cam3CpuValueLabel)
        commonPanel.Controls.Add(cam4CpuLabel)
        commonPanel.Controls.Add(cam4CpuValueLabel)
        commonPanel.Controls.Add(totalCpuLabel)
        commonPanel.Controls.Add(totalCpuValueLabel)
        commonPanel.Dock = DockStyle.Fill
        commonPanel.FlowDirection = FlowDirection.LeftToRight
        commonPanel.Location = New Point(0, 0)
        commonPanel.Margin = New Padding(0)
        commonPanel.Name = "commonPanel"
        commonPanel.Padding = New Padding(8, 8, 8, 4)
        commonPanel.Size = New Size(980, 35)
        commonPanel.WrapContents = False
        commonTitleLabel.AutoSize = True
        commonTitleLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        commonTitleLabel.Location = New Point(8, 11)
        commonTitleLabel.Margin = New Padding(0, 3, 14, 0)
        commonTitleLabel.Name = "commonTitleLabel"
        commonTitleLabel.Size = New Size(56, 15)
        commonTitleLabel.Text = "Common"
        audioListenLabel.AutoSize = True
        audioListenLabel.Location = New Point(78, 11)
        audioListenLabel.Margin = New Padding(0, 3, 6, 0)
        audioListenLabel.Name = "audioListenLabel"
        audioListenLabel.Size = New Size(71, 15)
        audioListenLabel.Text = "Listen Audio"
        audioListenComboBox.DropDownStyle = ComboBoxStyle.DropDownList
        audioListenComboBox.FormattingEnabled = True
        audioListenComboBox.Location = New Point(155, 8)
        audioListenComboBox.Margin = New Padding(0, 0, 18, 0)
        audioListenComboBox.Name = "audioListenComboBox"
        audioListenComboBox.Size = New Size(104, 23)
        cam1CpuLabel.AutoSize = True
        cam1CpuLabel.Location = New Point(277, 11)
        cam1CpuLabel.Margin = New Padding(0, 3, 6, 0)
        cam1CpuLabel.Name = "cam1CpuLabel"
        cam1CpuLabel.Size = New Size(62, 15)
        cam1CpuLabel.Text = "CAM1 CPU"
        cam1CpuValueLabel.AutoSize = True
        cam1CpuValueLabel.ForeColor = Color.DimGray
        cam1CpuValueLabel.Location = New Point(345, 11)
        cam1CpuValueLabel.Margin = New Padding(0, 3, 18, 0)
        cam1CpuValueLabel.Name = "cam1CpuValueLabel"
        cam1CpuValueLabel.Size = New Size(32, 15)
        cam1CpuValueLabel.Text = "0.0%"
        cam2CpuLabel.AutoSize = True
        cam2CpuLabel.Location = New Point(395, 11)
        cam2CpuLabel.Margin = New Padding(0, 3, 6, 0)
        cam2CpuLabel.Name = "cam2CpuLabel"
        cam2CpuLabel.Size = New Size(62, 15)
        cam2CpuLabel.Text = "CAM2 CPU"
        cam2CpuValueLabel.AutoSize = True
        cam2CpuValueLabel.ForeColor = Color.DimGray
        cam2CpuValueLabel.Location = New Point(463, 11)
        cam2CpuValueLabel.Margin = New Padding(0, 3, 18, 0)
        cam2CpuValueLabel.Name = "cam2CpuValueLabel"
        cam2CpuValueLabel.Size = New Size(32, 15)
        cam2CpuValueLabel.Text = "0.0%"
        cam3CpuLabel.AutoSize = True
        cam3CpuLabel.Location = New Point(513, 11)
        cam3CpuLabel.Margin = New Padding(0, 3, 6, 0)
        cam3CpuLabel.Name = "cam3CpuLabel"
        cam3CpuLabel.Size = New Size(62, 15)
        cam3CpuLabel.Text = "CAM3 CPU"
        cam3CpuValueLabel.AutoSize = True
        cam3CpuValueLabel.ForeColor = Color.DimGray
        cam3CpuValueLabel.Location = New Point(581, 11)
        cam3CpuValueLabel.Margin = New Padding(0, 3, 18, 0)
        cam3CpuValueLabel.Name = "cam3CpuValueLabel"
        cam3CpuValueLabel.Size = New Size(32, 15)
        cam3CpuValueLabel.Text = "0.0%"
        cam4CpuLabel.AutoSize = True
        cam4CpuLabel.Location = New Point(631, 11)
        cam4CpuLabel.Margin = New Padding(0, 3, 6, 0)
        cam4CpuLabel.Name = "cam4CpuLabel"
        cam4CpuLabel.Size = New Size(62, 15)
        cam4CpuLabel.Text = "CAM4 CPU"
        cam4CpuValueLabel.AutoSize = True
        cam4CpuValueLabel.ForeColor = Color.DimGray
        cam4CpuValueLabel.Location = New Point(699, 11)
        cam4CpuValueLabel.Margin = New Padding(0, 3, 18, 0)
        cam4CpuValueLabel.Name = "cam4CpuValueLabel"
        cam4CpuValueLabel.Size = New Size(32, 15)
        cam4CpuValueLabel.Text = "0.0%"
        totalCpuLabel.AutoSize = True
        totalCpuLabel.Location = New Point(749, 11)
        totalCpuLabel.Margin = New Padding(0, 3, 6, 0)
        totalCpuLabel.Name = "totalCpuLabel"
        totalCpuLabel.Size = New Size(45, 15)
        totalCpuLabel.Text = "PC CPU"
        totalCpuValueLabel.AutoSize = True
        totalCpuValueLabel.ForeColor = Color.DimGray
        totalCpuValueLabel.Location = New Point(800, 11)
        totalCpuValueLabel.Margin = New Padding(0, 3, 0, 0)
        totalCpuValueLabel.Name = "totalCpuValueLabel"
        totalCpuValueLabel.Size = New Size(32, 15)
        totalCpuValueLabel.Text = "0.0%"
        cameraGrid.ColumnCount = 2
        cameraGrid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
        cameraGrid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
        cameraGrid.Controls.Add(cam1GroupBox, 0, 0)
        cameraGrid.Controls.Add(cam2GroupBox, 1, 0)
        cameraGrid.Controls.Add(cam3GroupBox, 0, 1)
        cameraGrid.Controls.Add(cam4GroupBox, 1, 1)
        cameraGrid.Dock = DockStyle.Fill
        cameraGrid.Location = New Point(0, 35)
        cameraGrid.Margin = New Padding(0)
        cameraGrid.Name = "cameraGrid"
        cameraGrid.Padding = New Padding(6, 0, 6, 6)
        cameraGrid.RowCount = 2
        cameraGrid.RowStyles.Add(New RowStyle(SizeType.Percent, 50.0F))
        cameraGrid.RowStyles.Add(New RowStyle(SizeType.Percent, 50.0F))
        cameraGrid.Size = New Size(980, 853)
        cam1GroupBox.Controls.Add(leftRecorderControl)
        cam1GroupBox.Dock = DockStyle.Fill
        cam1GroupBox.Location = New Point(9, 3)
        cam1GroupBox.Margin = New Padding(3)
        cam1GroupBox.Name = "cam1GroupBox"
        cam1GroupBox.Padding = New Padding(4)
        cam1GroupBox.Size = New Size(479, 418)
        cam1GroupBox.TabStop = False
        cam1GroupBox.Text = "CAM1"
        leftRecorderControl.CameraName = "CAM1"
        leftRecorderControl.Dock = DockStyle.Fill
        leftRecorderControl.Location = New Point(4, 20)
        leftRecorderControl.Margin = New Padding(0)
        leftRecorderControl.Name = "leftRecorderControl"
        leftRecorderControl.SettingsKey = "CAM1"
        leftRecorderControl.Size = New Size(471, 394)
        cam2GroupBox.Controls.Add(rightRecorderControl)
        cam2GroupBox.Dock = DockStyle.Fill
        cam2GroupBox.Location = New Point(494, 3)
        cam2GroupBox.Margin = New Padding(3)
        cam2GroupBox.Name = "cam2GroupBox"
        cam2GroupBox.Padding = New Padding(4)
        cam2GroupBox.Size = New Size(477, 418)
        cam2GroupBox.TabStop = False
        cam2GroupBox.Text = "CAM2"
        rightRecorderControl.CameraName = "CAM2"
        rightRecorderControl.Dock = DockStyle.Fill
        rightRecorderControl.Location = New Point(4, 20)
        rightRecorderControl.Margin = New Padding(0)
        rightRecorderControl.Name = "rightRecorderControl"
        rightRecorderControl.SettingsKey = "CAM2"
        rightRecorderControl.Size = New Size(469, 394)
        cam3GroupBox.Controls.Add(thirdRecorderControl)
        cam3GroupBox.Dock = DockStyle.Fill
        cam3GroupBox.Location = New Point(9, 427)
        cam3GroupBox.Margin = New Padding(3)
        cam3GroupBox.Name = "cam3GroupBox"
        cam3GroupBox.Padding = New Padding(4)
        cam3GroupBox.Size = New Size(479, 417)
        cam3GroupBox.TabStop = False
        cam3GroupBox.Text = "CAM3"
        thirdRecorderControl.CameraName = "CAM3"
        thirdRecorderControl.Dock = DockStyle.Fill
        thirdRecorderControl.Location = New Point(4, 20)
        thirdRecorderControl.Margin = New Padding(0)
        thirdRecorderControl.Name = "thirdRecorderControl"
        thirdRecorderControl.SettingsKey = "CAM3"
        thirdRecorderControl.Size = New Size(471, 393)
        cam4GroupBox.Controls.Add(fourthRecorderControl)
        cam4GroupBox.Dock = DockStyle.Fill
        cam4GroupBox.Location = New Point(494, 427)
        cam4GroupBox.Margin = New Padding(3)
        cam4GroupBox.Name = "cam4GroupBox"
        cam4GroupBox.Padding = New Padding(4)
        cam4GroupBox.Size = New Size(477, 417)
        cam4GroupBox.TabStop = False
        cam4GroupBox.Text = "CAM4"
        fourthRecorderControl.CameraName = "CAM4"
        fourthRecorderControl.Dock = DockStyle.Fill
        fourthRecorderControl.Location = New Point(4, 20)
        fourthRecorderControl.Margin = New Padding(0)
        fourthRecorderControl.Name = "fourthRecorderControl"
        fourthRecorderControl.SettingsKey = "CAM4"
        fourthRecorderControl.Size = New Size(469, 393)
        Controls.Add(mainLayout)
        mainLayout.ResumeLayout(False)
        mainLayout.PerformLayout()
        commonPanel.ResumeLayout(False)
        commonPanel.PerformLayout()
        cameraGrid.ResumeLayout(False)
        cam1GroupBox.ResumeLayout(False)
        cam2GroupBox.ResumeLayout(False)
        cam3GroupBox.ResumeLayout(False)
        cam4GroupBox.ResumeLayout(False)
        ResumeLayout(False)
    End Sub
End Class
