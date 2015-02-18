namespace OrleansUnitTestContainer
{
    partial class SelectUnitTests
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
            this.UnitTestList = new System.Windows.Forms.ListView();
            this.unitTestHeader = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.label1 = new System.Windows.Forms.Label();
            this.random = new System.Windows.Forms.RadioButton();
            this.testClass = new System.Windows.Forms.RadioButton();
            this.button1 = new System.Windows.Forms.Button();
            this.button2 = new System.Windows.Forms.Button();
            this.groupBox1 = new System.Windows.Forms.GroupBox();
            this.alpha = new System.Windows.Forms.RadioButton();
            this.SelectAll = new System.Windows.Forms.CheckBox();
            this.groupBox1.SuspendLayout();
            this.SuspendLayout();
            // 
            // UnitTestList
            // 
            this.UnitTestList.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.unitTestHeader});
            this.UnitTestList.HideSelection = false;
            this.UnitTestList.Location = new System.Drawing.Point(26, 35);
            this.UnitTestList.Name = "UnitTestList";
            this.UnitTestList.Size = new System.Drawing.Size(294, 227);
            this.UnitTestList.Sorting = System.Windows.Forms.SortOrder.Ascending;
            this.UnitTestList.TabIndex = 0;
            this.UnitTestList.UseCompatibleStateImageBehavior = false;
            this.UnitTestList.View = System.Windows.Forms.View.Details;
            // 
            // unitTestHeader
            // 
            this.unitTestHeader.Text = "Unit Test";
            this.unitTestHeader.Width = 184;
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(23, 9);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(101, 13);
            this.label1.TabIndex = 2;
            this.label1.Text = "Unit Tests Available";
            // 
            // random
            // 
            this.random.AutoSize = true;
            this.random.Location = new System.Drawing.Point(19, 31);
            this.random.Name = "random";
            this.random.Size = new System.Drawing.Size(65, 17);
            this.random.TabIndex = 3;
            this.random.TabStop = true;
            this.random.Text = "Random";
            this.random.UseVisualStyleBackColor = true;
            this.random.CheckedChanged += new System.EventHandler(this.radioButton1_CheckedChanged);
            // 
            // testClass
            // 
            this.testClass.AutoSize = true;
            this.testClass.Location = new System.Drawing.Point(19, 54);
            this.testClass.Name = "testClass";
            this.testClass.Size = new System.Drawing.Size(74, 17);
            this.testClass.TabIndex = 5;
            this.testClass.TabStop = true;
            this.testClass.Text = "Test Class";
            this.testClass.UseVisualStyleBackColor = true;
            // 
            // button1
            // 
            this.button1.DialogResult = System.Windows.Forms.DialogResult.OK;
            this.button1.Location = new System.Drawing.Point(355, 194);
            this.button1.Name = "button1";
            this.button1.Size = new System.Drawing.Size(109, 23);
            this.button1.TabIndex = 7;
            this.button1.Text = "OK";
            this.button1.UseVisualStyleBackColor = true;
            this.button1.Click += new System.EventHandler(this.button1_Click);
            // 
            // button2
            // 
            this.button2.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.button2.Location = new System.Drawing.Point(355, 239);
            this.button2.Name = "button2";
            this.button2.Size = new System.Drawing.Size(109, 23);
            this.button2.TabIndex = 8;
            this.button2.Text = "Cancel";
            this.button2.UseVisualStyleBackColor = true;
            // 
            // groupBox1
            // 
            this.groupBox1.Controls.Add(this.alpha);
            this.groupBox1.Controls.Add(this.random);
            this.groupBox1.Controls.Add(this.testClass);
            this.groupBox1.Location = new System.Drawing.Point(342, 35);
            this.groupBox1.Name = "groupBox1";
            this.groupBox1.Size = new System.Drawing.Size(157, 118);
            this.groupBox1.TabIndex = 9;
            this.groupBox1.TabStop = false;
            this.groupBox1.Text = "Order By";
            // 
            // alpha
            // 
            this.alpha.AutoSize = true;
            this.alpha.Location = new System.Drawing.Point(19, 78);
            this.alpha.Name = "alpha";
            this.alpha.Size = new System.Drawing.Size(83, 17);
            this.alpha.TabIndex = 6;
            this.alpha.TabStop = true;
            this.alpha.Text = "Alphabetical";
            this.alpha.UseVisualStyleBackColor = true;
            // 
            // SelectAll
            // 
            this.SelectAll.AutoSize = true;
            this.SelectAll.Location = new System.Drawing.Point(361, 159);
            this.SelectAll.Name = "SelectAll";
            this.SelectAll.Size = new System.Drawing.Size(70, 17);
            this.SelectAll.TabIndex = 10;
            this.SelectAll.Text = "Select All";
            this.SelectAll.UseVisualStyleBackColor = true;
            this.SelectAll.CheckedChanged += new System.EventHandler(this.SelectAll_CheckedChanged);
            // 
            // SelectUnitTests
            // 
            this.AcceptButton = this.button1;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.button2;
            this.ClientSize = new System.Drawing.Size(547, 296);
            this.ControlBox = false;
            this.Controls.Add(this.SelectAll);
            this.Controls.Add(this.button2);
            this.Controls.Add(this.button1);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.UnitTestList);
            this.Controls.Add(this.groupBox1);
            this.Name = "SelectUnitTests";
            this.Text = "Organize UnitTests";
            this.groupBox1.ResumeLayout(false);
            this.groupBox1.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.ListView UnitTestList;
        private System.Windows.Forms.ColumnHeader unitTestHeader;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.RadioButton random;
        private System.Windows.Forms.RadioButton testClass;
        private System.Windows.Forms.Button button1;
        private System.Windows.Forms.Button button2;
        private System.Windows.Forms.GroupBox groupBox1;
        private System.Windows.Forms.RadioButton alpha;
        private System.Windows.Forms.CheckBox SelectAll;
    }
}