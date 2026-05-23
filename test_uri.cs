using System;
public class Program {
    public static void Main() {
        string href = "/valid";
        if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri)) {
            Console.WriteLine("Absolute: " + absoluteUri.ToString());
            Console.WriteLine("Scheme: " + absoluteUri.Scheme);
        } else {
            Console.WriteLine("Not Absolute");
        }
    }
}
