// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
namespace Server.Common.Unsafe.Collections.Generic;


internal enum InsertionBehavior : byte
{
    None,
    OverwriteExisting,
    ThrowOnExisting,
}