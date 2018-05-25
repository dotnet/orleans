using System;

namespace Utils
{
	public static class PrettyConsole
	{
		public static void Line(string text, ConsoleColor colour = ConsoleColor.White)
		{
			var originalColour = Console.ForegroundColor;
			Console.ForegroundColor = colour;
			Console.WriteLine(text);
			Console.ForegroundColor = originalColour;
		}
	}
}