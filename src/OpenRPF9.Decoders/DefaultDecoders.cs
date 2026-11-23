using System;
using System.Collections.Generic;
using System.IO;
namespace OpenRPF9.Decoders
{
    public static class DefaultDecoders
    {
        public static DecoderRegistry Create()
        {
            var registry = new DecoderRegistry();
            registry.Register(new TextureDictionaryDecoder());
            registry.Register(new DrawableDecoder());
            registry.Register(new ScriptDecoder());
            registry.Register(new MetaDecoder());
            registry.Register(new AudioDecoder());
            return registry;
        }
    }
}
