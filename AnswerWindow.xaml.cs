using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SleepReminder
{
    public partial class AnswerWindow : Window
    {
        private List<QuestionItem> _questions = new List<QuestionItem>();
        public bool IsAllCorrect { get; private set; } = false;
        public bool HasWrongAnswer { get; private set; } = false;

        // Boss题
        private string _bossQuestionString;
        private List<int> _bossCorrectPrimes;

        // 杀计算器定时器
        private System.Windows.Threading.DispatcherTimer _killCalcTimer;

        private class QuestionItem
        {
            public string Text { get; set; }
            public int CorrectAnswer { get; set; }
            public TextBox AnswerBox { get; set; }
        }

        public AnswerWindow()
        {
            InitializeComponent();
            GenerateQuestions();
            RenderQuestions();
            StartKillCalculator();
        }

        // ---------- 杀计算器进程 ----------
        private void StartKillCalculator()
        {
            _killCalcTimer = new System.Windows.Threading.DispatcherTimer();
            _killCalcTimer.Interval = TimeSpan.FromSeconds(1);
            _killCalcTimer.Tick += (s, e) => KillCalculator();
            _killCalcTimer.Start();
        }

        private void KillCalculator()
        {
            try
            {
                // 杀死 calc.exe（经典计算器）
                foreach (var p in Process.GetProcessesByName("calc"))
                {
                    try { p.Kill(); p.WaitForExit(500); } catch { }
                }
                // 杀死 Calculator.exe（Windows 10/11 新计算器）
                foreach (var p in Process.GetProcessesByName("Calculator"))
                {
                    try { p.Kill(); p.WaitForExit(500); } catch { }
                }
            }
            catch { /* 忽略异常 */ }
        }

        protected override void OnClosed(EventArgs e)
        {
            _killCalcTimer?.Stop();
            base.OnClosed(e);
        }

        // ---------- 普通题生成 ----------
        private void GenerateQuestions()
        {
            int count = App.IsDebugMode ? 1 : 10;
            Random rand = new Random();
            for (int i = 0; i < count; i++)
            {
                var q = GenerateSingleQuestion(rand);
                _questions.Add(q);
            }
        }

        private QuestionItem GenerateSingleQuestion(Random rand)
        {
            int type = rand.Next(0, 4);
            int a, b, result;
            string op;
            string questionText = "";

            switch (type)
            {
                case 0: // 乘法：三位数 × 两位数
                    a = rand.Next(100, 1000);
                    b = rand.Next(10, 100);
                    result = a * b;
                    op = "×";
                    questionText = $"{a} {op} {b}";
                    break;

                case 1: // 除法：四位数或五位数 ÷ 两位数，保证整除
                    int quotient = rand.Next(10, 1000);
                    int divisor = rand.Next(10, 100);
                    int dividend = quotient * divisor;
                    while (dividend < 1000 || dividend > 99999)
                    {
                        quotient = rand.Next(10, 1000);
                        divisor = rand.Next(10, 100);
                        dividend = quotient * divisor;
                    }
                    a = dividend;
                    b = divisor;
                    result = quotient;
                    op = "÷";
                    questionText = $"{a} {op} {b}";
                    break;

                case 2: // 加法：两个五位数相加
                    a = rand.Next(10000, 100000);
                    b = rand.Next(10000, 100000);
                    result = a + b;
                    op = "+";
                    questionText = $"{a} {op} {b}";
                    break;

                case 3: // 减法：两个五位数相减，保证非负
                    a = rand.Next(10000, 100000);
                    b = rand.Next(10000, 100000);
                    if (a < b) (a, b) = (b, a);
                    result = a - b;
                    op = "-";
                    questionText = $"{a} {op} {b}";
                    break;

                default:
                    a = rand.Next(10, 100);
                    b = rand.Next(10, 100);
                    result = a + b;
                    op = "+";
                    questionText = $"{a} {op} {b}";
                    break;
            }

            return new QuestionItem { Text = questionText, CorrectAnswer = result };
        }

        private void RenderQuestions()
        {
            spQuestions.Children.Clear();
            int index = 1;
            foreach (var q in _questions)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                var lbl = new TextBlock
                {
                    Text = $"{index}. {q.Text} = ",
                    FontSize = 18,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 280
                };
                var txt = new TextBox
                {
                    Width = 80,
                    Height = 30,
                    FontSize = 18,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Background = Brushes.DarkGray,
                    Foreground = Brushes.White,
                    BorderBrush = Brushes.Gray
                };
                q.AnswerBox = txt;
                panel.Children.Add(lbl);
                panel.Children.Add(txt);
                spQuestions.Children.Add(panel);
                index++;
            }
        }

        // ---------- 提交普通题 ----------
        private void SubmitButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var q in _questions)
            {
                if (string.IsNullOrWhiteSpace(q.AnswerBox.Text))
                {
                    tbResult.Text = "请填写所有题目的答案！";
                    tbResult.Visibility = Visibility.Visible;
                    return;
                }
                if (!int.TryParse(q.AnswerBox.Text, out _))
                {
                    tbResult.Text = "答案必须为整数！";
                    tbResult.Visibility = Visibility.Visible;
                    return;
                }
            }

            bool allCorrect = true;
            foreach (var q in _questions)
            {
                int userAns = int.Parse(q.AnswerBox.Text);
                if (userAns != q.CorrectAnswer)
                {
                    allCorrect = false;
                    break;
                }
            }

            if (allCorrect)
            {
                ShowBossQuestion();
            }
            else
            {
                HasWrongAnswer = true;
                tbResult.Text = "有题目回答错误！答题机会已取消。";
                tbResult.Visibility = Visibility.Visible;
                btnSubmit.IsEnabled = false;
                btnCancel.IsEnabled = false;
                foreach (var q in _questions) q.AnswerBox.IsEnabled = false;
                var timer = new System.Windows.Threading.DispatcherTimer();
                timer.Interval = TimeSpan.FromSeconds(3);
                timer.Tick += (s, args) => { timer.Stop(); this.DialogResult = false; Close(); };
                timer.Start();
            }
        }

        // ---------- Boss 题生成 ----------
        private void GenerateBossQuestion()
        {
            Random rand = new Random();
            int count = rand.Next(8, 10);
            List<int> numbers = new List<int>();
            List<int> primes = new List<int>();

            int[] primePool = {
                11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 67, 71, 73, 79, 83, 89, 97,
                101, 103, 107, 109, 113, 127, 131, 137, 139, 149, 151, 157, 163, 167, 173, 179, 181, 191, 193, 197, 199
            };
            int[] compositePool = {
                10, 12, 14, 15, 16, 18, 20, 21, 22, 24, 25, 26, 27, 28, 30, 32, 33, 34, 35, 36, 38, 39, 40, 42, 44, 45, 46, 48, 49, 50,
                51, 52, 54, 55, 56, 57, 58, 60, 62, 63, 64, 65, 66, 68, 69, 70, 72, 74, 75, 76, 77, 78, 80, 81, 82, 84, 85, 86, 87, 88,
                90, 91, 92, 93, 94, 95, 96, 98, 99, 100, 102, 104, 105, 106, 108, 110, 111, 112, 114, 115, 116, 117, 118, 119, 120,
                121, 122, 123, 124, 125, 126, 128, 129, 130, 132, 133, 134, 135, 136, 138, 140, 141, 142, 143, 144, 145, 146, 147, 148, 150
            };

            int primeCount = rand.Next(4, Math.Min(8, count));
            for (int i = 0; i < primeCount; i++)
            {
                int p = primePool[rand.Next(primePool.Length)];
                if (!numbers.Contains(p))
                {
                    numbers.Add(p);
                    primes.Add(p);
                }
                else i--;
            }

            while (numbers.Count < count)
            {
                int c = compositePool[rand.Next(compositePool.Length)];
                if (!numbers.Contains(c))
                {
                    numbers.Add(c);
                }
            }

            for (int i = 0; i < numbers.Count; i++)
            {
                int idx = rand.Next(i, numbers.Count);
                (numbers[i], numbers[idx]) = (numbers[idx], numbers[i]);
            }

            List<string> parts = new List<string>();
            for (int i = 0; i < numbers.Count; i++)
            {
                parts.Add(numbers[i].ToString());
                if (i < numbers.Count - 1)
                {
                    char letter = (char)rand.Next('A', 'Z' + 1);
                    parts.Add(letter.ToString());
                }
            }
            _bossQuestionString = string.Join("", parts);
            _bossCorrectPrimes = primes.OrderBy(p => p).ToList();
        }

        private void ShowBossQuestion()
        {
            svNormal.Visibility = Visibility.Collapsed;
            btnSubmit.Visibility = Visibility.Collapsed;
            btnCancel.Visibility = Visibility.Collapsed;
            tbResult.Visibility = Visibility.Collapsed;
            svBoss.Visibility = Visibility.Visible;

            GenerateBossQuestion();
            tbBossQuestion.Text = $"请从以下字符串中提取出所有质数（用空格分隔）：\n\n{_bossQuestionString}";
            tbBossResult.Visibility = Visibility.Collapsed;
            txtBossAnswer.Clear();
            txtBossAnswer.IsEnabled = true;
            btnBossSubmit.IsEnabled = true;
            txtBossAnswer.Focus();
        }

        // ---------- Boss 题提交 ----------
        private void BossSubmit_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtBossAnswer.Text))
            {
                tbBossResult.Text = "请输入答案！";
                tbBossResult.Visibility = Visibility.Visible;
                return;
            }

            string[] parts = txtBossAnswer.Text.Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            List<int> userNumbers = new List<int>();
            foreach (string part in parts)
            {
                if (int.TryParse(part, out int num))
                {
                    userNumbers.Add(num);
                }
                else
                {
                    tbBossResult.Text = $"“{part}”不是有效数字，请重新输入！";
                    tbBossResult.Visibility = Visibility.Visible;
                    return;
                }
            }

            userNumbers.Sort();
            // 确保正确答案列表已排序（已在生成时排序，但为防万一）
            if (userNumbers.SequenceEqual(_bossCorrectPrimes))
            {
                IsAllCorrect = true;
                this.DialogResult = true;
                Close();
            }
            else
            {
                HasWrongAnswer = true;
                tbBossResult.Text = "Boss 题答错！答题机会已取消。";
                tbBossResult.Visibility = Visibility.Visible;
                txtBossAnswer.IsEnabled = false;
                btnBossSubmit.IsEnabled = false;

                var timer = new System.Windows.Threading.DispatcherTimer();
                timer.Interval = TimeSpan.FromSeconds(3);
                timer.Tick += (s, args) => { timer.Stop(); this.DialogResult = false; Close(); };
                timer.Start();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            Close();
        }
    }
}