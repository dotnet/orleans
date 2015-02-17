namespace NodeManager
{
    partial class Main
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.txtOrleanServer = new System.Windows.Forms.TextBox();
            this.label7 = new System.Windows.Forms.Label();
            this.txtOrleanPort = new System.Windows.Forms.TextBox();
            this.label8 = new System.Windows.Forms.Label();
            this.BtnSend = new System.Windows.Forms.Button();
            this.label4 = new System.Windows.Forms.Label();
            this.txtStatus = new System.Windows.Forms.RichTextBox();
            this.txtWait = new System.Windows.Forms.TextBox();
            this.label1 = new System.Windows.Forms.Label();
            this.SuspendLayout();
            // 
            // txtOrleanServer
            // 
            this.txtOrleanServer.Location = new System.Drawing.Point(157, 14);
            this.txtOrleanServer.Name = "txtOrleanServer";
            this.txtOrleanServer.Size = new System.Drawing.Size(100, 20);
            this.txtOrleanServer.TabIndex = 20;
            this.txtOrleanServer.Text = "localhost";
            // 
            // label7
            // 
            this.label7.AutoSize = true;
            this.label7.Location = new System.Drawing.Point(18, 14);
            this.label7.Name = "label7";
            this.label7.Size = new System.Drawing.Size(141, 13);
            this.label7.TabIndex = 19;
            this.label7.Text = "Orlean Directory Service IP :";
            // 
            // txtOrleanPort
            // 
            this.txtOrleanPort.Location = new System.Drawing.Point(157, 37);
            this.txtOrleanPort.Name = "txtOrleanPort";
            this.txtOrleanPort.Size = new System.Drawing.Size(100, 20);
            this.txtOrleanPort.TabIndex = 18;
            this.txtOrleanPort.Text = "8090";
            // 
            // label8
            // 
            this.label8.AutoSize = true;
            this.label8.Location = new System.Drawing.Point(120, 40);
            this.label8.Name = "label8";
            this.label8.Size = new System.Drawing.Size(29, 13);
            this.label8.TabIndex = 17;
            this.label8.Text = "Port:";
            // 
            // BtnSend
            // 
            this.BtnSend.Location = new System.Drawing.Point(157, 108);
            this.BtnSend.Name = "BtnSend";
            this.BtnSend.Size = new System.Drawing.Size(75, 32);
            this.BtnSend.TabIndex = 16;
            this.BtnSend.Text = "Start";
            this.BtnSend.UseVisualStyleBackColor = true;
            this.BtnSend.Click += new System.EventHandler(this.BtnSend_Click);
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(18, 150);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(43, 13);
            this.label4.TabIndex = 22;
            this.label4.Text = "Status :";
            // 
            // txtStatus
            // 
            this.txtStatus.Location = new System.Drawing.Point(3, 166);
            this.txtStatus.Name = "txtStatus";
            this.txtStatus.Size = new System.Drawing.Size(285, 339);
            this.txtStatus.TabIndex = 21;
            this.txtStatus.Text = "";
            this.txtStatus.TextChanged += new System.EventHandler(this.txtStatus_TextChanged);
            // 
            // txtWait
            // 
            this.txtWait.Location = new System.Drawing.Point(157, 63);
            this.txtWait.Name = "txtWait";
            this.txtWait.Size = new System.Drawing.Size(100, 20);
            this.txtWait.TabIndex = 24;
            this.txtWait.Text = "10";
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(75, 66);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(74, 13);
            this.label1.TabIndex = 25;
            this.label1.Text = "Wait time(sec)";
            // 
            // Main
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(297, 517);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.txtWait);
            this.Controls.Add(this.label4);
            this.Controls.Add(this.txtStatus);
            this.Controls.Add(this.txtOrleanServer);
            this.Controls.Add(this.label7);
            this.Controls.Add(this.txtOrleanPort);
            this.Controls.Add(this.label8);
            this.Controls.Add(this.BtnSend);
            this.Name = "Main";
            this.Text = "Node Manager";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.TextBox txtOrleanServer;
        private System.Windows.Forms.Label label7;
        private System.Windows.Forms.TextBox txtOrleanPort;
        private System.Windows.Forms.Label label8;
        private System.Windows.Forms.Button BtnSend;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.RichTextBox txtStatus;
        private System.Windows.Forms.TextBox txtWait;
        private System.Windows.Forms.Label label1;
    }
}

