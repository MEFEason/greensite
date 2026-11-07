// 全局别名：项目同时启用 WPF 与 WinForms，消除同名控件类型歧义。
// 业务代码里需要 WinForms 类型时仍用完全限定名（如 System.Windows.Forms.NotifyIcon）。
global using System.IO;

global using UserControl = System.Windows.Controls.UserControl;
global using Button = System.Windows.Controls.Button;
global using CheckBox = System.Windows.Controls.CheckBox;
global using ComboBox = System.Windows.Controls.ComboBox;
global using TextBox = System.Windows.Controls.TextBox;
global using Control = System.Windows.Controls.Control;
global using Label = System.Windows.Controls.Label;
global using DataGrid = System.Windows.Controls.DataGrid;
global using MenuItem = System.Windows.Controls.MenuItem;
global using ContextMenu = System.Windows.Controls.ContextMenu;
global using Orientation = System.Windows.Controls.Orientation;
global using MessageBox = System.Windows.MessageBox;
