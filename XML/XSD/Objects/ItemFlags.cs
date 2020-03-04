// ------------------------------------------------------------------------------
//  <auto-generated>
//    Generated by Xsd2Code++. Version 5.1.46.0. www.xsd2code.com
//  </auto-generated>
// ------------------------------------------------------------------------------
#pragma warning disable
namespace Hybrasyl.Xml
{
using System;
using System.Diagnostics;
using System.Xml.Serialization;
using System.Collections;
using System.Xml.Schema;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.ComponentModel.DataAnnotations;
using System.Xml;
using System.Collections.Generic;

[System.FlagsAttribute()]
[System.CodeDom.Compiler.GeneratedCodeAttribute("System.Xml", "4.8.3752.0")]
[Serializable]
[XmlTypeAttribute(AnonymousType=true, Namespace="http://www.hybrasyl.com/XML/Hybrasyl/2020-02")]
public enum ItemFlags
{
    Bound = 1,
    Depositable = 2,
    Enchantable = 4,
    Consecratable = 8,
    Tailorable = 16,
    Smithable = 32,
    Exchangeable = 64,
    Vendorable = 128,
    Perishable = 256,
    Unique = 512,
    Master = 1024,
    [XmlEnumAttribute("Unique-Equipped")]
    UniqueEquipped = 2048,
}
}
#pragma warning restore
