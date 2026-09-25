using System;
using System.Windows.Forms;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Media;
using System.IO;

namespace ShutdownDialog
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length < 2) return;

            int delay = int.Parse(args[0]);
            string message = args[1];
            bool isReboot = args.Length > 2 && args[2] == "reboot";

            Application.EnableVisualStyles();
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.Run(new ShutdownForm(delay, message, isReboot));
        }
    }

    public class ShutdownForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool FlashWindow(IntPtr hWnd, bool bInvert);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private System.Windows.Forms.Timer countdownTimer;
        private System.Windows.Forms.Timer flashTimer;
        private System.Windows.Forms.Timer tickTimer;
        private int secondsLeft;
        private Panel mainPanel;
        private Label iconLabel;
        private Label messageLabel;
        private Label countdownLabel;
        private Button cancelButton;
        private NotifyIcon trayIcon;
        private SoundPlayer tickSound;
        private bool isReboot;
        private bool cancelled = false;
        private bool flashState = false;

        public ShutdownForm(int delay, string message, bool isReboot)
        {
            secondsLeft = delay;
            this.isReboot = isReboot;

            // Генерируем звук тика
            tickSound = CreateTickSound();

            // Иконка в трее
            trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Warning,
                Visible = true,
                Text = "Предупреждение о выключении"
            };
            trayIcon.Click += (s, e) => { ForceToFrontAggressively(); };

            // Современный стиль Windows 11
            this.Text = "Предупреждение системы";
            this.Size = new System.Drawing.Size(500, 240);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.TopMost = true;
            this.FormBorderStyle = FormBorderStyle.None;
            this.BackColor = Color.FromArgb(32, 32, 32);
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = true;
            this.WindowState = FormWindowState.Normal;

            // Закругленные углы (Windows 11 style)
            this.Region = System.Drawing.Region.FromHrgn(CreateRoundRectRgn(0, 0, this.Width, this.Height, 12, 12));

            // Главная панель
            mainPanel = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(500, 240),
                BackColor = Color.FromArgb(32, 32, 32)
            };

            // Иконка предупреждения
            iconLabel = new Label
            {
                Text = "⚠",
                Location = new Point(30, 40),
                Size = new Size(60, 60),
                Font = new Font("Segoe UI", 36F),
                ForeColor = Color.FromArgb(255, 185, 0),
                TextAlign = ContentAlignment.MiddleCenter
            };

            // Сообщение
            messageLabel = new Label
            {
                Text = message,
                Location = new Point(110, 40),
                Size = new Size(360, 50),
                Font = new Font("Segoe UI", 14F),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleLeft
            };

            // Таймер обратного отсчета
            countdownLabel = new Label
            {
                Text = $"Осталось: {secondsLeft} сек",
                Location = new Point(110, 95),
                Size = new Size(360, 30),
                Font = new Font("Segoe UI Semibold", 12F),
                ForeColor = Color.FromArgb(200, 200, 200),
                TextAlign = ContentAlignment.MiddleLeft
            };

            // Кнопка отмены (современный стиль)
            cancelButton = new Button
            {
                Text = "Отменить",
                Location = new Point(300, 160),
                Size = new Size(170, 50),
                Font = new Font("Segoe UI", 11F),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            cancelButton.FlatAppearance.BorderSize = 0;
            cancelButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 140, 235);
            cancelButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 100, 195);
            cancelButton.Click += CancelButton_Click;

            mainPanel.Controls.Add(iconLabel);
            mainPanel.Controls.Add(messageLabel);
            mainPanel.Controls.Add(countdownLabel);
            mainPanel.Controls.Add(cancelButton);
            this.Controls.Add(mainPanel);

            this.Load += ShutdownForm_Load;
            this.FormClosing += ShutdownForm_FormClosing;

            countdownTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            countdownTimer.Tick += CountdownTimer_Tick;

            // Таймер для мигания трея и окна
            flashTimer = new System.Windows.Forms.Timer { Interval = 500 };
            flashTimer.Tick += FlashTimer_Tick;

            // Таймер для тиканья каждую секунду
            tickTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            tickTimer.Tick += TickTimer_Tick;

            // Звуковое уведомление
            SystemSounds.Exclamation.Play();
        }

        private SoundPlayer CreateTickSound()
        {
            // Создаем простой WAV файл с коротким щелчком
            var stream = new MemoryStream();
            var writer = new BinaryWriter(stream);

            int sampleRate = 8000;
            short numChannels = 1;
            short bitsPerSample = 16;
            int numSamples = sampleRate / 20; // 50ms

            // WAV заголовок
            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + numSamples * 2);
            writer.Write(new[] { 'W', 'A', 'V', 'E' });
            writer.Write(new[] { 'f', 'm', 't', ' ' });
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(numChannels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * numChannels * bitsPerSample / 8);
            writer.Write((short)(numChannels * bitsPerSample / 8));
            writer.Write(bitsPerSample);
            writer.Write(new[] { 'd', 'a', 't', 'a' });
            writer.Write(numSamples * 2);

            // Генерируем короткий щелчок
            for (int i = 0; i < numSamples; i++)
            {
                double t = (double)i / sampleRate;
                double frequency = 1000;
                double amplitude = 8000 * Math.Exp(-t * 50); // Затухающий звук
                short sample = (short)(amplitude * Math.Sin(2 * Math.PI * frequency * t));
                writer.Write(sample);
            }

            stream.Position = 0;
            return new SoundPlayer(stream);
        }

        private void ShutdownForm_Load(object sender, EventArgs e)
        {
            ForceToFrontAggressively();
            countdownTimer.Start();
            flashTimer.Start();
            tickTimer.Start();
        }

        private void ForceToFrontAggressively()
        {
            IntPtr currentForeground = GetForegroundWindow();
            IntPtr thisWindow = this.Handle;

            uint currentThreadId = GetCurrentThreadId();
            uint foregroundThreadId = GetWindowThreadProcessId(currentForeground, IntPtr.Zero);

            AttachThreadInput(currentThreadId, foregroundThreadId, true);

            ShowWindow(thisWindow, SW_RESTORE);
            ShowWindow(thisWindow, SW_SHOW);
            SetWindowPos(thisWindow, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            BringWindowToTop(thisWindow);
            SetActiveWindow(thisWindow);
            SetForegroundWindow(thisWindow);

            AttachThreadInput(currentThreadId, foregroundThreadId, false);

            this.Activate();
            this.Focus();
        }

        private void FlashTimer_Tick(object sender, EventArgs e)
        {
            // Мигание окна в панели задач
            FlashWindow(this.Handle, true);

            // Мигание иконки в трее
            flashState = !flashState;
            trayIcon.Icon = flashState ? SystemIcons.Warning : SystemIcons.Information;
        }

        private void TickTimer_Tick(object sender, EventArgs e)
        {
            // Тиканье через SoundPlayer
            tickSound?.Play();
        }

        private void CountdownTimer_Tick(object sender, EventArgs e)
        {
            secondsLeft--;
            countdownLabel.Text = $"Осталось: {secondsLeft} сек";

            if (secondsLeft <= 0)
            {
                countdownTimer.Stop();
                flashTimer.Stop();
                tickTimer.Stop();

                if (!cancelled)
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "shutdown.exe",
                        Arguments = isReboot ? "/r /t 0 /f" : "/s /t 0 /f",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(startInfo);
                }

                this.Close();
            }
        }

        private void CancelButton_Click(object sender, EventArgs e)
        {
            cancelled = true;
            countdownTimer.Stop();
            flashTimer.Stop();
            tickTimer.Stop();
            this.Close();
        }

        private void ShutdownForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            countdownTimer?.Stop();
            flashTimer?.Stop();
            tickTimer?.Stop();
            tickSound?.Dispose();
            trayIcon?.Dispose();
        }
    }
}