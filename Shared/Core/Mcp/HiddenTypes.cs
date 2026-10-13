using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

namespace Shared.Mcp;

// Hides top-level types of a reference from scripts: in the copy of its metadata Roslyn reads, they get
// [Microsoft.CodeAnalysis.Embedded], which keeps a type, its members and its extension methods out of
// lookup. The constructor is borrowed: renaming the RuntimeCompatibilityAttribute TypeRef the C# compiler
// puts in every assembly makes its .ctor EmbeddedAttribute's. Roslyn matches [Embedded] by name without
// resolving the TypeRef, and reads no reference's RuntimeCompatibility. Only CustomAttribute rows are
// inserted, which no column points at, and bytes appended to #Strings and #Blob, so no index widens.
internal static class HiddenTypes
{
    // The tables a CustomAttribute's Parent can point into.
    private static readonly TableIndex[] ParentTables =
    [
        TableIndex.MethodDef, TableIndex.Field, TableIndex.TypeRef, TableIndex.TypeDef, TableIndex.Param,
        TableIndex.InterfaceImpl, TableIndex.MemberRef, TableIndex.Module, TableIndex.DeclSecurity,
        TableIndex.Property, TableIndex.Event, TableIndex.StandAloneSig, TableIndex.ModuleRef, TableIndex.TypeSpec,
        TableIndex.Assembly, TableIndex.AssemblyRef, TableIndex.File, TableIndex.ExportedType,
        TableIndex.ManifestResource, TableIndex.GenericParam, TableIndex.GenericParamConstraint, TableIndex.MethodSpec
    ];

    // Without the .ctor Patch borrows (an assembly from another compiler, mostly), none of an assembly's
    // types can be hidden.
    public static bool CanPatch(MetadataReader md) => !BorrowedCtor(md).IsNil;

    // The patched metadata, or null when it can't be made. Never freed: ReleaseShared runs as the game
    // exits, possibly while a compile still reads it.
    public static (IntPtr Pointer, int Size)? Patch(string path, ISet<string> types)
    {
        try
        {
            return Rewrite(path, types);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string FullName(MetadataReader md, TypeDefinition type)
    {
        var ns = md.GetString(type.Namespace);
        var name = md.GetString(type.Name);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    // RuntimeCompatibilityAttribute's parameterless .ctor, in an assembly of one module.
    private static MemberReferenceHandle BorrowedCtor(MetadataReader md)
    {
        var typeRef = md.TypeReferences.FirstOrDefault(h =>
            IsNamed(md, md.GetTypeReference(h), "System.Runtime.CompilerServices", "RuntimeCompatibilityAttribute"));
        if (typeRef.IsNil || md.AssemblyFiles.Any(h => md.GetAssemblyFile(h).ContainsMetadata)) return default;

        return md.MemberReferences.FirstOrDefault(h =>
        {
            var member = md.GetMemberReference(h);
            return member.Parent == typeRef && Parameterless(md, member);
        });
    }

    private static unsafe (IntPtr, int)? Rewrite(string path, ISet<string> types)
    {
        using var file = File.OpenRead(path);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadata();
        var root = metadata.Pointer;
        var md = new MetadataReader(root, metadata.Length);

        var ctor = BorrowedCtor(md);
        if (ctor.IsNil) return null;
        var typeRef = (TypeReferenceHandle)md.GetMemberReference(ctor).Parent;

        // The Parent of each new row, a HasCustomAttribute coded index: TypeDef is tag 3. Rows come in order.
        var parents = new List<int>();
        foreach (var handle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(handle);
            if (type.GetDeclaringType().IsNil && types.Contains(FullName(md, type)))
                parents.Add(MetadataTokens.GetRowNumber(handle) << 5 | 3);
        }
        if (parents.Count == 0) return null;

        // The stream headers, past the version string, the flags and their count.
        var streams = new List<(int Header, int Offset, int Size, string Name)>();
        var header = 20 + *(int*)(root + 12);
        for (int n = *(ushort*)(root + header - 2); n > 0; n--)
        {
            var end = header + 8;
            while (root[end] != 0)
                end++;
            var name = Encoding.ASCII.GetString(root + header + 8, end - header - 8);
            streams.Add((header, *(int*)(root + header), *(int*)(root + header + 4), name));
            header = Align(end + 1);
        }
        var tables = streams.FindIndex(s => s.Name == "#~");
        var strings = streams.FindIndex(s => s.Name == "#Strings");
        var blobs = streams.FindIndex(s => s.Name == "#Blob");
        if (tables < 0 || strings < 0 || blobs < 0) return null;

        // Appended: the two names, and an argument-less attribute value (01 00 00 00) unless one is there.
        var emptyValue = md.CustomAttributes
            .Select(h => md.GetCustomAttribute(h).Value)
            .FirstOrDefault(v => BlobIs(md, v, 1, 0, 0, 0));
        var addStrings = "Microsoft.CodeAnalysis\0EmbeddedAttribute\0"u8.ToArray();
        byte[] addBlob = emptyValue.IsNil ? [4, 1, 0, 0, 0] : [];
        var nsAt = streams[strings].Size;
        var nameAt = nsAt + "Microsoft.CodeAnalysis\0".Length;
        var valueAt = emptyValue.IsNil ? streams[blobs].Size : MetadataTokens.GetHeapOffset(emptyValue);

        // Column widths. A heap index takes 4 bytes when its HeapSizes bit is set; a coded index takes 2
        // while every table it can point into has fewer rows than 2^(16 - tag bits).
        var tablesAt = streams[tables].Offset;
        var heapSizes = root[tablesAt + 6];
        var stringWidth = (heapSizes & 1) != 0 ? 4 : 2;
        var blobWidth = (heapSizes & 4) != 0 ? 4 : 2;
        var parentWidth = ParentTables.Max(md.GetTableRowCount) < 1 << 11 ? 2 : 4;
        var typeWidth = Math.Max(md.GetTableRowCount(TableIndex.MethodDef), md.GetTableRowCount(TableIndex.MemberRef)) < 1 << 13 ? 2 : 4;

        // The row counts follow the tables stream's header, one per table present, in table order.
        var caRowSize = md.GetTableRowSize(TableIndex.CustomAttribute);
        var caRows = md.GetTableRowCount(TableIndex.CustomAttribute);
        var present = *(ulong*)(root + tablesAt + 8);
        var caRowsAt = tablesAt + 24 + 4 * PopCount(present & ((1UL << (int)TableIndex.CustomAttribute) - 1));
        if (parentWidth + typeWidth + blobWidth != caRowSize || *(int*)(root + caRowsAt) != caRows) return null;

        var newSizes = streams.Select(s => s.Size).ToArray();
        newSizes[tables] += parents.Count * caRowSize;
        newSizes[strings] += addStrings.Length;
        newSizes[blobs] += addBlob.Length;
        for (var i = 0; i < newSizes.Length; i++)
            newSizes[i] = Align(newSizes[i]);
        // With 2-byte indices a heap must stay under 64 KB.
        if (stringWidth == 2 && newSizes[strings] >= 0x10000) return null;
        if (blobWidth == 2 && newSizes[blobs] >= 0x10000) return null;

        // The copy: the header as it is, then the streams in the order they sit in, each moved by what the
        // ones before it grew.
        var order = Enumerable.Range(0, streams.Count).OrderBy(i => streams[i].Offset).ToArray();
        var headerSize = streams[order[0]].Offset;
        var total = headerSize + newSizes.Sum();
        var output = Marshal.AllocHGlobal(total);
        var newRoot = (byte*)output;
        var dst = newRoot;
        Copy(root, ref dst, headerSize);
        foreach (var i in order)
        {
            var src = root + streams[i].Offset;
            var start = dst;
            if (i == tables)
            {
                // Sorted by Parent: each new row goes after the existing ones of its type.
                var caAt = md.GetTableMetadataOffset(TableIndex.CustomAttribute) - tablesAt;
                Copy(src, ref dst, caAt);
                var oldRow = src + caAt;
                var oldEnd = oldRow + caRows * caRowSize;
                foreach (var parent in parents)
                {
                    while (oldRow < oldEnd && Read(oldRow, parentWidth) <= parent)
                    {
                        Copy(oldRow, ref dst, caRowSize);
                        oldRow += caRowSize;
                    }
                    Write(ref dst, parentWidth, parent);
                    Write(ref dst, typeWidth, MetadataTokens.GetRowNumber(ctor) << 3 | 3);   // CustomAttributeType: MemberRef is tag 3
                    Write(ref dst, blobWidth, valueAt);
                }
                Copy(oldRow, ref dst, (int)(src + streams[i].Size - oldRow));
                *(int*)(start + caRowsAt - tablesAt) = caRows + parents.Count;

                // TypeRef columns: ResolutionScope, Name, Namespace. Row numbers start at 1.
                var typeRefRowSize = md.GetTableRowSize(TableIndex.TypeRef);
                var nameColumn = start + md.GetTableMetadataOffset(TableIndex.TypeRef) - tablesAt
                    + MetadataTokens.GetRowNumber(typeRef) * typeRefRowSize - 2 * stringWidth;
                Write(ref nameColumn, stringWidth, nameAt);
                Write(ref nameColumn, stringWidth, nsAt);
            }
            else
            {
                Copy(src, ref dst, streams[i].Size);
                if (i == strings) Append(ref dst, addStrings);
                if (i == blobs) Append(ref dst, addBlob);
            }

            while (dst < start + newSizes[i])
                *dst++ = 0;
            *(int*)(newRoot + streams[i].Header) = (int)(start - newRoot);
            *(int*)(newRoot + streams[i].Header + 4) = newSizes[i];
        }

        if (ReadsBack(newRoot, total, caRows + parents.Count, typeRef)) return (output, total);
        Marshal.FreeHGlobal(output);
        return null;
    }

    // Whether the patched copy parses, with the rows added and the TypeRef renamed.
    private static unsafe bool ReadsBack(byte* root, int size, int caRows, TypeReferenceHandle typeRef)
    {
        try
        {
            var md = new MetadataReader(root, size);
            return md.GetTableRowCount(TableIndex.CustomAttribute) == caRows
                && IsNamed(md, md.GetTypeReference(typeRef), "Microsoft.CodeAnalysis", "EmbeddedAttribute");
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static bool IsNamed(MetadataReader md, TypeReference type, string ns, string name) =>
        md.StringComparer.Equals(type.Namespace, ns) && md.StringComparer.Equals(type.Name, name);

    private static bool Parameterless(MetadataReader md, MemberReference member) =>
        md.StringComparer.Equals(member.Name, ".ctor") && BlobIs(md, member.Signature, 0x20, 0, 1);   // instance, no parameters, void

    // Not a list pattern: on .NET Framework that needs System.Index.
    private static bool BlobIs(MetadataReader md, BlobHandle blob, params byte[] bytes) =>
        md.GetBlobBytes(blob).SequenceEqual(bytes);

    private static int Align(int x) => (x + 3) & ~3;

    private static int PopCount(ulong bits)
    {
        var count = 0;
        while (bits != 0)
        {
            bits &= bits - 1;
            count++;
        }
        return count;
    }

    private static unsafe int Read(byte* p, int width) => width == 2 ? *(ushort*)p : *(int*)p;

    private static unsafe void Write(ref byte* p, int width, int value)
    {
        if (width == 2)
            *(ushort*)p = (ushort)value;
        else
            *(int*)p = value;
        p += width;
    }

    private static unsafe void Copy(byte* src, ref byte* dst, int count)
    {
        Buffer.MemoryCopy(src, dst, count, count);
        dst += count;
    }

    private static unsafe void Append(ref byte* dst, byte[] bytes)
    {
        foreach (var b in bytes)
            *dst++ = b;
    }
}
