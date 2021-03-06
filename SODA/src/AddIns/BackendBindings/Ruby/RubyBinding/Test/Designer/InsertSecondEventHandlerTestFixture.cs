﻿// <file>
//     <copyright see="prj:///doc/copyright.txt"/>
//     <license see="prj:///doc/license.txt"/>
//     <owner name="Matthew Ward" email="mrward@users.sourceforge.net"/>
//     <version>$Revision: 5343 $</version>
// </file>

using System;
using ICSharpCode.RubyBinding;
using ICSharpCode.FormsDesigner;
using ICSharpCode.SharpDevelop.Dom;
using NUnit.Framework;
using RubyBinding.Tests.Utils;

namespace RubyBinding.Tests.Designer
{
	/// <summary>
	/// Tests the RubyDesignerGenerator adds an extra new line between the previous event handler
	/// and the new one inserted.
	/// </summary>
	[TestFixture]
	public class InsertSecondEventHandlerTestFixture : InsertEventHandlerTestFixtureBase
	{
		public override void AfterSetUpFixture()
		{						
			MockEventDescriptor mockEventDescriptor = new MockEventDescriptor("Click");
			generator.InsertComponentEvent(null, mockEventDescriptor, "button1_click", String.Empty, out file, out position);
			insertedEventHandler = generator.InsertComponentEvent(null, mockEventDescriptor, "button2_click", String.Empty, out file, out position);
		}
		
		[Test]
		public void ExpectedCodeAfterEventHandlerInserted()
		{
			string expectedCode =
				"require \"System.Windows.Forms\"\r\n" +
				"\r\n" +
				"class MainForm < Form\r\n" +
				"\tdef initialize()\r\n" +
				"\t\tself.InitializeComponents()\r\n" +
				"\tend\r\n" +
				"\t\r\n" +
				"\tdef InitializeComponents()\r\n" +
				"\t\t@button1 = System::Windows::Forms::Button.new()\r\n" +
				"\t\tself.Controls.Add(@button1)\r\n" +
				"\tend\r\n" +
				"\r\n" +				
				"\tdef button1_click(sender, e)\r\n" +
				"\t\t\r\n" +
				"\tend\r\n" +
				"\r\n" +
				"\tdef button2_click(sender, e)\r\n" +
				"\t\t\r\n" +
				"\tend\r\n" +
				"end";
			
			Assert.AreEqual(expectedCode, viewContent.DesignerCodeFileContent);
		}
		
		protected override string GetTextEditorCode()
		{
			return
				"require \"System.Windows.Forms\"\r\n" +
				"\r\n" +
				"class MainForm < Form\r\n" +
				"\tdef initialize()\r\n" +
				"\t\tself.InitializeComponents()\r\n" +
				"\tend\r\n" +
				"\t\r\n" +
				"\tdef InitializeComponents()\r\n" +
				"\t\t@button1 = System::Windows::Forms::Button.new()\r\n" +
				"\t\tself.Controls.Add(@button1)\r\n" +
				"\tend\r\n" +
				"end";
		}
	}
}
