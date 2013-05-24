﻿// GTA2.NET
// 
// File: ProgressForm.cs
// Created: 05.04.2013
// 
// 
// Copyright (C) 2010-2013 Hiale
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software
// and associated documentation files (the "Software"), to deal in the Software without restriction,
// including without limitation the rights to use, copy, modify, merge, publish, distribute,
// sublicense, and/or sell copies of the Software, and to permit persons to whom the Software
// is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies
// or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR
// IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// 
// Grand Theft Auto (GTA) is a registred trademark of Rockstar Games.
using System;
using System.Windows.Forms;

namespace Hiale.GTA2NET.WinUI
{
    public partial class ProgressForm : Form
    {
        private const string DefaultMessage = "Please wait...";

        public string Message
        {
            get { return lblMessage.Text; }
            set
            {
                if (string.IsNullOrEmpty(value))
                    value = DefaultMessage;
                lblMessage.Text = value;
            }
        }

        public int ProgressValue
        {
            get { return progressBar.Value; }
            set { progressBar.Value = value; }
        }

        public event EventHandler<EventArgs> Canceled;

        public ProgressForm()
        {
            InitializeComponent();
        }

        private void BtnCancelClick(object sender, EventArgs e)
        {
            if (Canceled != null)
                Canceled(this, EventArgs.Empty);
        }
    }
}
