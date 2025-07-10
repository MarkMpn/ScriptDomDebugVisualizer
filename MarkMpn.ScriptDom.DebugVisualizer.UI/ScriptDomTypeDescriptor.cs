using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Design;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MarkMpn.ScriptDom.DebugVisualizer.UI
{
    class ScriptDomTypeDescriptor : CustomTypeDescriptor
    {
        private readonly TSqlFragment _fragment;

        public ScriptDomTypeDescriptor(TSqlFragment fragment)
        {
            _fragment = fragment;
        }

        public override AttributeCollection GetAttributes()
        {
            return new AttributeCollection();
        }

        public override PropertyDescriptorCollection GetProperties()
        {
            return new PropertyDescriptorCollection(_fragment
                .GetType()
                .GetProperties()
                .Where(p => p.GetIndexParameters().Length == 0)
                .Select(p => new ScriptDomPropertyDescriptor(_fragment, p))
                .ToArray());
        }

        public override PropertyDescriptorCollection GetProperties(Attribute[] attributes)
        {
            return GetProperties();
        }

        public override object GetPropertyOwner(PropertyDescriptor pd)
        {
            return _fragment;
        }
    }

    class ScriptDomPropertyDescriptor : PropertyDescriptor
    {
        private readonly object _target;
        private readonly PropertyInfo _property;
        private readonly object _value;

        public ScriptDomPropertyDescriptor(TSqlFragment fragment, PropertyInfo property)
            : base(property.Name, GetAttributes(property.PropertyType, property.DeclaringType))
        {
            _target = fragment;
            _property = property;
            _value = null;
        }

        public ScriptDomPropertyDescriptor(object target, string name, object value)
            : base(name, GetAttributes(value.GetType(), value.GetType()))
        {
            _target = target;
            _property = null;
            _value = value;
        }

        public override Type ComponentType => typeof(ScriptDomTypeDescriptor);

        public override bool IsReadOnly => !typeof(TSqlFragment).IsAssignableFrom(PropertyType);

        public override Type PropertyType => _property?.PropertyType ?? _value.GetType();

        private static Attribute[] GetAttributes(Type propertyType, Type declaringType)
        {
            var attrs = new List<Attribute>();

            attrs.Add(new CategoryAttribute(declaringType.Name));

            if (propertyType.IsEnum)
            {
                attrs.Add(new TypeConverterAttribute(typeof(EnumConverter)));
            }
            else if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(IList<>))
            {
                attrs.Add(new TypeConverterAttribute(typeof(ListConverter)));
            }
            else if (typeof(TSqlFragment).IsAssignableFrom(propertyType))
            {
                attrs.Add(new TypeConverterAttribute(typeof(ScriptDomTypeConverter)));
                attrs.Add(new EditorAttribute(typeof(ScriptDomEditor), typeof(UITypeEditor)));
            }

            if (!typeof(TSqlFragment).IsAssignableFrom(propertyType))
                attrs.Add(new ReadOnlyAttribute(true));

            return attrs.ToArray();
        }

        public override bool CanResetValue(object component)
        {
            throw new NotImplementedException();
        }

        public override object GetValue(object component)
        {
            if (_value != null)
                return _value;

            if (component is CustomTypeDescriptor desc)
                component = desc.GetPropertyOwner(this);

            return _property.GetValue(component, null);
        }

        public override void ResetValue(object component)
        {
            throw new NotImplementedException();
        }

        public override void SetValue(object component, object value)
        {
            throw new NotImplementedException();
        }

        public override bool ShouldSerializeValue(object component)
        {
            return false;
        }

        public static string GetName(ITypeDescriptorContext context)
        {
            if (context.PropertyDescriptor is ScriptDomPropertyDescriptor pd && pd._property == null)
            {
                if (pd._value is string[] strings)
                    return String.Join(", ", strings);

                return $"({context.PropertyDescriptor.PropertyType.Name})";
            }

            return $"({context.PropertyDescriptor.Name})";
        }

        public override TypeConverter Converter
        {
            get
            {
                if (typeof(System.Collections.IList).IsAssignableFrom(PropertyType))
                    return new ListConverter();
                else if (PropertyType.IsEnum)
                    return new EnumConverter(PropertyType);
                else if (typeof(TSqlFragment).IsAssignableFrom(PropertyType))
                    return new ScriptDomTypeConverter();

                return base.Converter;
            }
        }

        public override object GetEditor(Type editorBaseType)
        {
            if (typeof(TSqlFragment).IsAssignableFrom(PropertyType))
                return new ScriptDomEditor();

            return base.GetEditor(editorBaseType);
        }
    }

    class ListConverter : TypeConverter
    {
        public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
        {
            return destinationType == typeof(string);
        }

        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
        {
            if (value == null)
                return String.Empty;

            if (value is ICustomTypeDescriptor desc)
                value = desc.GetPropertyOwner(null);

            var list = (System.Collections.IList)value;

            if (list.Count == 0)
                return "(None)";

            return ScriptDomPropertyDescriptor.GetName(context);
        }

        public override bool GetPropertiesSupported(ITypeDescriptorContext context)
        {
            var value = context.PropertyDescriptor.GetValue(context.Instance);

            if (value is ICustomTypeDescriptor desc)
                value = desc.GetPropertyOwner(null);

            var list = (System.Collections.IList)value;
            return list != null && list.Count > 0;
        }

        public override PropertyDescriptorCollection GetProperties(ITypeDescriptorContext context, object value, Attribute[] attributes)
        {
            if (value is ICustomTypeDescriptor desc)
                value = desc.GetPropertyOwner(null);

            var list = (System.Collections.IList)value;

            return new PropertyDescriptorCollection(list.Cast<object>().Select((item, i) => new ListItemPropertyDescriptor(list, item, i)).ToArray());
        }
    }

    class ListItemPropertyDescriptor : ScriptDomPropertyDescriptor
    {
        public ListItemPropertyDescriptor(System.Collections.IList list, object item, int index) : base(list, GetPropertyName(list, item, index), GetPropertyValue(item))
        {
        }

        private static string GetPropertyName(System.Collections.IList list, object item, int index)
        {
            return index.ToString().PadLeft((int)Math.Ceiling(Math.Log10(list.Count)), '0');
        }

        private static object GetPropertyValue(object item)
        {
            if (item.GetType().IsArray && ((Array)item).Length == 1)
                item = ((Array)item).GetValue(0);

            return item;
        }
    }

    class ScriptDomTypeConverter : TypeConverter
    {
        public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
        {
            return destinationType == typeof(string);
        }

        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
        {
            if (value == null)
                return "(Null)";

            return ScriptDomPropertyDescriptor.GetName(context);
        }

        public override PropertyDescriptorCollection GetProperties(ITypeDescriptorContext context, object value, Attribute[] attributes)
        {
            return new ScriptDomTypeDescriptor((TSqlFragment)value).GetProperties(attributes);
        }
    }

    class ScriptDomEditor : UITypeEditor
    {
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context)
        {
            return UITypeEditorEditStyle.Modal;
        }

        public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
        {
            var site = (ScriptDomUserControl)provider.GetService(typeof(ScriptDomUserControl));
            _ = site.SelectFragment((TSqlFragment)value);
            return value;
        }
    }
}
