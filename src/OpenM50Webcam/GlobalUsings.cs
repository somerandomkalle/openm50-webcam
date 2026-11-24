// WPF projects drop System.IO from the implicit usings (Path is ambiguous with
// System.Windows.Shapes.Path); none of our files import System.Windows.Shapes.
global using System.IO;
