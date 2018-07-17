﻿using System;
using ICSharpCode.CodeConverter.CSharp;
using ICSharpCode.CodeConverter.Shared;
using ICSharpCode.CodeConverter.VB;

namespace ICSharpCode.CodeConverter
{
    public static class CodeConverter
    {
        public static ConversionResult Convert(CodeWithOptions code)
        {
            if (!IsSupportedSource(code.FromLanguage, code.FromLanguageVersion))
                return new ConversionResult(new NotSupportedException($"Source language {code.FromLanguage} {code.FromLanguageVersion} is not supported!"));
            if (!IsSupportedTarget(code.ToLanguage, code.ToLanguageVersion))
                return new ConversionResult(new NotSupportedException($"Target language {code.ToLanguage} {code.ToLanguageVersion} is not supported!"));
            if (code.FromLanguage == code.ToLanguage && code.FromLanguageVersion != code.ToLanguageVersion)
                return new ConversionResult(new NotSupportedException($"Converting from {code.FromLanguage} {code.FromLanguageVersion} to {code.ToLanguage} {code.ToLanguageVersion} is not supported!"));

            switch (code.FromLanguage) {
                case "C#":
                    switch (code.ToLanguage) {
                        case "Visual Basic":
                            return ProjectConversion.ConvertText<CSToVBConversion>(code.Text, code.References);
                    }
                    break;
                case "Visual Basic":
                    switch (code.ToLanguage) {
                        case "C#":
                            return ProjectConversion.ConvertText<VBToCSConversion>(code.Text, code.References);
                    }
                    break;

            }
            return new ConversionResult(new NotSupportedException($"Converting from {code.FromLanguage} {code.FromLanguageVersion} to {code.ToLanguage} {code.ToLanguageVersion} is not supported!"));
        }

        static bool IsSupportedTarget(string toLanguage, int toLanguageVersion)
        {
            return (toLanguage == "Visual Basic" && toLanguageVersion == 14) || (toLanguage == "C#" && toLanguageVersion == 6);
        }

        static bool IsSupportedSource(string fromLanguage, int fromLanguageVersion)
        {
            return (fromLanguage == "C#" && fromLanguageVersion == 6) || (fromLanguage == "Visual Basic" && fromLanguageVersion == 14);
        }
    }
}
