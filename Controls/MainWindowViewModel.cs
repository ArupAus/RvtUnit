﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using IntegrationTesting.Models;
using System.Collections.ObjectModel;
using GalaSoft.MvvmLight.Command;
using System.IO;
using NUnit.Core;
using NUnit.Util;
using System.Collections;
using System.Reflection;
using IntegrationTesting.Helpers;
using System.Windows.Media;
using System.Windows;

namespace IntegrationTesting.Controls
{
    public class MainWindowViewModel
    {
		private Brush _currentBrush;

        public MainWindowViewModel()
        {
            DLLs = new ObservableCollection<TestableDll>();

            PopulateDLLs();

            RunTestCommand = new RelayCommand<string>(this.OnRunTest);
            RunAllTestsCommand = new RelayCommand(this.OnRunAllTests);
        }

        public ObservableCollection<TestableDll> DLLs { get; set; }

        public RelayCommand<string> RunTestCommand { get; set; }

        public RelayCommand RunAllTestsCommand { get; set; }

		public Window LinkedWindow { get; set; }

		private string SelectFolder(string title, string defaultPath, Window owner)
		{
			string selectedFolder = null;
			var dialog = new System.Windows.Forms.FolderBrowserDialog();
			if (!string.IsNullOrEmpty(defaultPath)) { dialog.SelectedPath = defaultPath; }
			dialog.Description = title;
			System.Windows.Forms.DialogResult result = (owner != null) ? dialog.ShowDialog(new Wpf32Window(owner)) : dialog.ShowDialog();
			if (result == System.Windows.Forms.DialogResult.OK)
			{
				selectedFolder = dialog.SelectedPath;
			}
			return selectedFolder;
		}

		private void PopulateDLLs()
		{
			// get executing assembly
			string thisAssemblyName = Assembly.GetExecutingAssembly().ManifestModule.Name;

			string path = SelectFolder("Test dlls location", Path.GetDirectoryName(thisAssemblyName), LinkedWindow);

			if (String.IsNullOrEmpty(path)) { return; }

			// adding from dev 
			foreach (string dll in System.IO.Directory.GetFiles(path, "*" + ".dll"))
			{
				if (!dll.EndsWith(thisAssemblyName))
				{
					TestableDll testableDll = new TestableDll(dll);
					testableDll.Tests = GetTestsFromDll(dll);
					DLLs.Add(testableDll);
				}
			}

		}

		private void OnRunTest(string testDll)
		{
			string originalDllName = testDll;
			LinkedWindow.Close();
			// Shadow copy to temp folder
			string tempFile = Path.GetTempFileName();
			tempFile = tempFile.Replace(Path.GetExtension(tempFile), Path.GetExtension(testDll));
			File.Copy(testDll, tempFile, true);

			//Copy the PDB to get stack trace
			string destPdbFile = tempFile.Replace(Path.GetExtension(tempFile), ".pdb");
			string srcPdb = testDll.Replace(Path.GetExtension(testDll), ".pdb");
			File.Copy(srcPdb, destPdbFile, true);

			testDll = tempFile;

			// Copy Moq.dll and dependencies to temp folder
			CopyRequiredLibsToTemp(tempFile);

			SimpleTestFilter filter = new SimpleTestFilter(DLLs.FirstOrDefault(d => d.FullPath == originalDllName).Tests);
			TestResult testResult = RunTestsInAssemblies(new List<string>() { testDll }, filter);

			// Save the result to xml file:
			string resultFile = Path.GetDirectoryName(testDll) + @"\TestResult.xml";
			XmlResultWriter resultWriter = new XmlResultWriter(resultFile);
			resultWriter.SaveTestResult(testResult);

			// show results dialog
			string extraMsg = "Result file can be found at " + resultFile;
			TestResultViewerViewModel vm = new TestResultViewerViewModel(testResult, extraMsg);
			TestResultViewer viewer = new TestResultViewer(vm);
			//viewer.Owner = LinkedWindow;
			GeneralHelper.SetRevitAsWindowOwner(viewer);
			viewer.ShowDialog();

			// cleanup
			File.Delete(tempFile);
			File.Delete(destPdbFile);
		}

        private void OnRunAllTests()
        {
			LinkedWindow.Close();
			
			List<string> copiedAssemblies = new List<string>();

            foreach (TestableDll dll in DLLs)
            {
                // Shadow copy to temp folder
                string tempFile = Path.GetTempFileName();
                tempFile = tempFile.Replace(Path.GetExtension(tempFile), Path.GetExtension(dll.FullPath));
                File.Copy(dll.FullPath, tempFile, true);

                //Copy the PDB to get stack trace
                string destPdbFile = tempFile.Replace(Path.GetExtension(tempFile), ".pdb");
                string srcPdb = dll.FullPath.Replace(Path.GetExtension(dll.FullPath), ".pdb");
                File.Copy(srcPdb, destPdbFile, true);

                copiedAssemblies.Add(tempFile);
            }

            // Copy Moq.dll and dependencies to temp folder
			CopyRequiredLibsToTemp(DLLs.First().FullPath);

            TestResult testResult = RunTestsInAssemblies(copiedAssemblies, TestFilter.Empty);

            // Save the result to xml file:
            string resultFile = Path.GetDirectoryName(DLLs.First().FullPath) + @"\TestResult.xml";
            XmlResultWriter resultWriter = new XmlResultWriter(resultFile);
            resultWriter.SaveTestResult(testResult);

            // show results dialog
            string extraMsg = "Result file can be found at " + resultFile;
            TestResultViewerViewModel vm = new TestResultViewerViewModel(testResult, extraMsg);
            TestResultViewer viewer = new TestResultViewer(vm);
            //viewer.Owner = LinkedWindow;
			GeneralHelper.SetRevitAsWindowOwner(viewer);
            viewer.ShowDialog();

            // cleanup
            foreach (string dll in copiedAssemblies)
            {
                File.Delete(dll);
                File.Delete(Path.ChangeExtension(dll, ".pdp"));
            }
        }

		private void CopyRequiredLibsToTemp(string path)
		{
			File.Delete(Path.Combine(Path.GetDirectoryName(path), "Moq.dll"));
			File.Delete(Path.Combine(Path.GetDirectoryName(path), "Castle.Core.dll"));
			File.Delete(Path.Combine(Path.GetDirectoryName(path), "TechTalk.SpecFlow.dll"));
			File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path), "Moq.dll"), Resources.Resources.Moq);
			File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path), "Castle.Core.dll"), Resources.Resources.Castle_Core);
			File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path), "TechTalk.SpecFlow.dll"), Resources.Resources.TechTalk_SpecFlow);
		}

        private TestResult RunTestsInAssemblies(IList assemblies, ITestFilter testfilter)
        {
            // Note: in order for nunit to unload assembly (not lock it)
            // we are using a modified version of nunit.core.dll (and the dependent nunit.interfaces.dll)
            // if the nunit dlls are updated this is what needs to be changed:
            //      in TestAssemblyBuilder class in Load method,
            //      change "assembly = Assembly.Load(assemblyName);"
            //      to "assembly = Assembly.Load(File.ReadAllBytes(path));" 
            TestPackage theTestPackage = new TestPackage("All Dlls", assemblies);
			theTestPackage.Settings.Add("UseThreadedRunner", false);
			theTestPackage.Settings.Add("DomainUsage", DomainUsage.None);
            RemoteTestRunner testRunner = new RemoteTestRunner();
            testRunner.Load(theTestPackage);
            TestResult testResult = testRunner.Run(NullListener.NULL, testfilter, false, LoggingThreshold.Off);

            // Dispose
            testRunner.Unload();
            testRunner.Dispose();
            return testResult;
        }

		private List<IntegrationTesting.Models.Test> GetTestsFromDll(string dll)
		{
			List<IntegrationTesting.Models.Test> tests = new List<IntegrationTesting.Models.Test>();
			try
			{
				var assembly = Assembly.Load(GetByteArrayForFile(dll));
				//get testfixture classes in assembly.
				var testTypes = from t in assembly.GetTypes()
								let attributes = t.GetCustomAttributes(typeof(NUnit.Framework.TestFixtureAttribute), true)
								where attributes != null && attributes.Length > 0
								orderby t.Name
								select t;
				foreach (var type in testTypes)
				{
					//get test method in class.
					var testMethods = from m in type.GetMethods()
									  let attributes = m.GetCustomAttributes(typeof(NUnit.Framework.TestAttribute), true)
									  where attributes != null && attributes.Length > 0
									  orderby m.Name
									  select m;
					Brush brush = PickAlternateBrush();
					foreach (var method in testMethods)
					{
						tests.Add(new IntegrationTesting.Models.Test() { TestName = method.Name, IsChecked = true, Brush = brush });
					}
				}
			}
			catch { }
			return tests;
		}

		private static byte[] GetByteArrayForFile(string filename)
		{
			FileStream fs = new FileStream(filename, FileMode.Open);
			byte[] buffer = new byte[(int)fs.Length];
			fs.Read(buffer, 0, buffer.Length);
			fs.Close();

			return buffer;
		}

		private Brush PickAlternateBrush()
		{
			if (_currentBrush == Brushes.LightGray)
			{
				_currentBrush = Brushes.White;
			}
			else
			{
				_currentBrush = Brushes.LightGray;
			}

			return _currentBrush;
		}

	}

	class Wpf32Window : System.Windows.Forms.IWin32Window
	{
		public IntPtr Handle { get; private set; }

		public Wpf32Window(Window wpfWindow)
		{
			Handle = new System.Windows.Interop.WindowInteropHelper(wpfWindow).Handle;
		}
	}

}
