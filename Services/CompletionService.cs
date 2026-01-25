using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace ProtoTestTool.Services
{
    public class CompletionItem
    {
        public string label { get; set; } = "";
        public int kind { get; set; } // Monaco Kind (Method=1, Field=3, Class=5, Property=9)
        public string insertText { get; set; } = "";
        public string detail { get; set; } = "";
        public string documentation { get; set; } = "";
    }

    public class CompletionService
    {
        public static string GenerateCompletionJson(IEnumerable<Type> types)
        {
            var items = new List<CompletionItem>();

            foreach (var type in types)
            {
                if (type == null) continue;

                // Add Type itself
                items.Add(new CompletionItem
                {
                    label = type.Name,
                    kind = 6, // Class
                    insertText = type.Name,
                    detail = type.Namespace ?? "",
                    documentation = "Class " + type.FullName
                });

                // Add Properties
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                {
                    items.Add(new CompletionItem
                    {
                        label = prop.Name,
                        kind = 9, // Property
                        insertText = prop.Name,
                        detail = type.Name + "." + prop.Name,
                        documentation = prop.PropertyType.Name
                    });
                }

                // Add Methods
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object)))
                {
                    items.Add(new CompletionItem
                    {
                        label = method.Name,
                        kind = 1, // Method
                        insertText = method.Name,
                        detail = type.Name + "." + method.Name,
                        documentation = method.ToString() ?? ""
                    });
                }
            }

            // Global Keywords (Optional)
            items.Add(new CompletionItem { label = "ScriptGlobals", kind = 6, insertText = "ScriptGlobals", detail = "Global Context" });

            return JsonSerializer.Serialize(items);
        }
    }
}
