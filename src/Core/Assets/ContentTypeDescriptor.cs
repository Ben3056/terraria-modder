using System;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Describes one content type's array layout for TypeExtension to resize.
    /// One descriptor per content type: items, tiles, NPCs, etc.
    /// AssetSystem builds the descriptor and passes it to TypeExtension.Apply().
    /// </summary>
    internal struct ContentTypeDescriptor
    {
        /// <summary>
        /// The ID class containing the Sets nested class (e.g. typeof(Terraria.ID.ItemID)).
        /// TypeExtension scans all static array fields in IdClass.Sets.
        /// </summary>
        public Type IdClass;

        /// <summary>The original vanilla array size (before extension).</summary>
        public int VanillaCount;

        /// <summary>The new extended array size.</summary>
        public int ExtendedCount;

        /// <summary>
        /// Additional named static array fields to resize, beyond those in IdClass.Sets.
        /// Examples: TextureAssets.Item, Lang._itemNameCache, Main.itemAnimations.
        /// Each field is found by type + field name. Missing fields are silently skipped.
        /// </summary>
        public NamedField[] AdditionalFields;

        public struct NamedField
        {
            public Type DeclaringType;
            public string FieldName;

            public NamedField(Type declaringType, string fieldName)
            {
                DeclaringType = declaringType;
                FieldName = fieldName;
            }
        }
    }
}
