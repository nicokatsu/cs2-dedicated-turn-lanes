using System.Text;

namespace PocketTurnLanes.Tool.Json
{
    internal static class JsonStringBuilder
    {
        public static void AppendProperty(StringBuilder builder, string name, string value)
        {
            AppendString(builder, name);
            builder.Append(':');
            AppendString(builder, value);
        }

        public static void AppendProperty(StringBuilder builder, string name, int value)
        {
            AppendString(builder, name);
            builder.Append(':');
            builder.Append(value);
        }

        public static void AppendProperty(StringBuilder builder, string name, bool value)
        {
            AppendString(builder, name);
            builder.Append(':');
            builder.Append(value ? "true" : "false");
        }

        public static void AppendString(StringBuilder builder, string value)
        {
            builder.Append('"');
            if (!string.IsNullOrEmpty(value))
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    switch (c)
                    {
                        case '\\':
                            builder.Append("\\\\");
                            break;
                        case '"':
                            builder.Append("\\\"");
                            break;
                        case '\b':
                            builder.Append("\\b");
                            break;
                        case '\f':
                            builder.Append("\\f");
                            break;
                        case '\n':
                            builder.Append("\\n");
                            break;
                        case '\r':
                            builder.Append("\\r");
                            break;
                        case '\t':
                            builder.Append("\\t");
                            break;
                        default:
                            if (c < 32)
                            {
                                builder.Append("\\u");
                                builder.Append(((int)c).ToString("x4"));
                            }
                            else
                            {
                                builder.Append(c);
                            }

                            break;
                    }
                }
            }

            builder.Append('"');
        }
    }
}
