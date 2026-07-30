using System.IO;
using System.Text;

namespace TikMan.Core.Discovery;

/// <summary>A <see cref="StringWriter"/> that reports UTF-8.
///
/// <para>⚠️ Exists because <see cref="System.Xml.XmlWriter"/> takes its declared encoding from the writer
/// it is given, and the default StringWriter reports UTF-16 – so a document built in memory comes out
/// starting <c>&lt;?xml version="1.0" encoding="utf-16"?&gt;</c> even though it is then saved as UTF-8.
/// The bytes and the declaration disagree, and a strict parser is entitled to reject the file.</para></summary>
internal sealed class Utf8StringWriter : StringWriter
{
    public override Encoding Encoding => Encoding.UTF8;
}
