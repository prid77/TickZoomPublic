﻿// <file>
//     <copyright see="prj:///doc/copyright.txt"/>
//     <license see="prj:///doc/license.txt"/>
//     <owner name="Matthew Ward" email="mrward@users.sourceforge.net"/>
//     <version>$Revision: 5343 $</version>
// </file>

using System;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.ComponentModel.Design.Serialization;
using System.Drawing;
using System.Windows.Forms;
using ICSharpCode.RubyBinding;
using NUnit.Framework;
using RubyBinding.Tests.Utils;

namespace RubyBinding.Tests.Designer
{
	[TestFixture]
	 public class GeneratePanelFormTestFixture
	{
		string generatedRubyCode;
		
		[TestFixtureSetUp]
		public void SetUpFixture()
		{
			using (DesignSurface designSurface = new DesignSurface(typeof(Form))) {
				IDesignerHost host = (IDesignerHost)designSurface.GetService(typeof(IDesignerHost));
				IEventBindingService eventBindingService = new MockEventBindingService(host);
				host.AddService(typeof(IEventBindingService), eventBindingService);

				Form form = (Form)host.RootComponent;
				form.ClientSize = new Size(284, 264);
				
				PropertyDescriptorCollection descriptors = TypeDescriptor.GetProperties(form);
				PropertyDescriptor namePropertyDescriptor = descriptors.Find("Name", false);
				namePropertyDescriptor.SetValue(form, "MainForm");
				
				Panel panel = (Panel)host.CreateComponent(typeof(Panel), "panel1");
				panel.Location = new Point(10, 15);
				panel.Anchor = AnchorStyles.Bottom | AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
				panel.TabIndex = 0;
				panel.Size = new Size(100, 120);
				TextBox textBox = (TextBox)host.CreateComponent(typeof(TextBox), "textBox1");
				textBox.Location = new Point(5, 5);
				textBox.TabIndex = 0;
				textBox.Size = new Size(110, 20);
				panel.Controls.Add(textBox);
				
				// Add an event handler to the panel to check that this code is generated
				// before the text box is initialized.
				EventDescriptorCollection events = TypeDescriptor.GetEvents(panel);
				EventDescriptor clickEvent = events.Find("Click", false);
				PropertyDescriptor clickEventProperty = eventBindingService.GetEventProperty(clickEvent);
				clickEventProperty.SetValue(panel, "Panel1Click");

				form.Controls.Add(panel);
				
				DesignerSerializationManager serializationManager = new DesignerSerializationManager(host);
				using (serializationManager.CreateSession()) {					
					RubyCodeDomSerializer serializer = new RubyCodeDomSerializer("    ");
					generatedRubyCode = serializer.GenerateInitializeComponentMethodBody(host, serializationManager, 1);
				}
			}
		}
				
		[Test]
		public void GeneratedCode()
		{
			string expectedCode =
				"    @panel1 = System::Windows::Forms::Panel.new()\r\n" +
				"    @textBox1 = System::Windows::Forms::TextBox.new()\r\n" +
				"    @panel1.SuspendLayout()\r\n" +
				"    self.SuspendLayout()\r\n" +
				"    # \r\n" +
				"    # panel1\r\n" +
				"    # \r\n" +
				"    @panel1.Anchor = System::Windows::Forms::AnchorStyles.Top | System::Windows::Forms::AnchorStyles.Bottom | System::Windows::Forms::AnchorStyles.Left | System::Windows::Forms::AnchorStyles.Right\r\n" +
				"    @panel1.Controls.Add(@textBox1)\r\n" +
				"    @panel1.Location = System::Drawing::Point.new(10, 15)\r\n" +
				"    @panel1.Name = \"panel1\"\r\n" +
				"    @panel1.Size = System::Drawing::Size.new(100, 120)\r\n" +
				"    @panel1.TabIndex = 0\r\n" +
				"    @panel1.Click { |sender, e| self.Panel1Click(sender, e) }\r\n" +
				"    # \r\n" +
				"    # textBox1\r\n" +
				"    # \r\n" +
				"    @textBox1.Location = System::Drawing::Point.new(5, 5)\r\n" +
				"    @textBox1.Name = \"textBox1\"\r\n" +
				"    @textBox1.Size = System::Drawing::Size.new(110, 20)\r\n" +
				"    @textBox1.TabIndex = 0\r\n" +
				"    # \r\n" +
				"    # MainForm\r\n" +
				"    # \r\n" +
				"    self.ClientSize = System::Drawing::Size.new(284, 264)\r\n" +
				"    self.Controls.Add(@panel1)\r\n" +
				"    self.Name = \"MainForm\"\r\n" +
				"    @panel1.ResumeLayout(false)\r\n" +
				"    @panel1.PerformLayout()\r\n" +
				"    self.ResumeLayout(false)\r\n";
			
			Assert.AreEqual(expectedCode, generatedRubyCode, generatedRubyCode);
		}
	}
}
