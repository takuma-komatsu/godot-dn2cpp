using System;
using System.Collections.Generic;

namespace GodotTools.Export
{
    internal sealed class Dn2CppOptimizationOptions
    {
        private readonly bool _trimReflection;
        private readonly bool _trimGodotClasses;
        private readonly bool _sharedGenerics;
        private readonly bool _compressMetadata;
        private readonly string _strippingLevel;

        public Dn2CppOptimizationOptions(Func<string, bool?> getOption, int strippingLevel = 0)
        {
            // Existing presets lack these keys and must inherit the export defaults.
            _trimReflection = getOption("trim_reflection") ?? true;
            _trimGodotClasses = getOption("trim_godot_classes") ?? true;
            _sharedGenerics = getOption("shared_generics") ?? true;
            _compressMetadata = getOption("compress_metadata") ?? true;
            _strippingLevel = strippingLevel switch
            {
                0 => "high",
                1 => "medium",
                _ => throw new InvalidOperationException("dn2cpp: Stripping Level must be High or Medium."),
            };
        }

        public void AppendArguments(List<string> arguments)
        {
            arguments.Add("--stripping-level");
            arguments.Add(_strippingLevel);
            if (_trimReflection)
                arguments.Add("--trim-reflection");
            if (_trimGodotClasses)
                arguments.Add("--trim-godot-classes");
            if (!_sharedGenerics)
                arguments.Add("--no-shared-generics");
            if (!_compressMetadata)
                arguments.Add("--no-metadata-compression");
        }

        public void AppendExtraArguments(List<string> arguments, string[] extraArguments)
        {
            for (int i = 0; i < extraArguments.Length; i++)
            {
                if (extraArguments[i] != "--stripping-level")
                {
                    arguments.Add(extraArguments[i]);
                    continue;
                }
                if (++i == extraArguments.Length || extraArguments[i] != _strippingLevel)
                    throw new InvalidOperationException(
                        "dn2cpp: --stripping-level in extra_transpile_args must match the export preset's Stripping Level.");
            }
        }
    }
}
