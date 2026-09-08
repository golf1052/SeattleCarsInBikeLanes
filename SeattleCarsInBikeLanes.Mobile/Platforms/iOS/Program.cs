using System.Diagnostics;
using ObjCRuntime;
using UIKit;

namespace SeattleCarsInBikeLanes.Mobile;

public class Program
{
	// This is the main entry point of the application.
	static void Main(string[] args)
	{
		// if you want to use a different Application Delegate class from "AppDelegate"
		// you can specify it here.
		UIApplication.Main(args, null, typeof(AppDelegate));
		// try {
			
		// } catch (Exception ex) {
		// 	Debug.WriteLine(ex);
		// 	Debug.WriteLine(ex.StackTrace);
		// 	throw;
		// }
	}
}
