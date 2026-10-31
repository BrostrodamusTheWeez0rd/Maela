using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Maela
{
    internal sealed class EmojiEntry
    {
        public readonly string Group;
        public readonly string Text;
        public readonly string FileName;
        public readonly string Sha256;

        public EmojiEntry(string group, string text, string fileName, string sha256)
        {
            Group = group;
            Text = text;
            FileName = fileName;
            Sha256 = sha256;
        }
    }

    /// <summary>
    /// Downloadable, open-source assets. Nothing here ships inside Maela.exe: on first run (and
    /// whenever the pinned version changes or a file is damaged) the files are fetched from the
    /// project's official source and every file is checked against the SHA-256 pinned below.
    /// A file without a matching hash is never installed.
    ///
    /// Twemoji graphics: Copyright Twitter, Inc. and other contributors, CC-BY 4.0,
    /// https://github.com/jdecked/twemoji
    /// </summary>
    internal static class EmojiPack
    {
        public const string Name = "Twemoji";
        public const string Version = "17.0.3";
        public const string License = "CC-BY 4.0";
        public const string Homepage = "https://github.com/jdecked/twemoji";
        public const string BaseUrl = "https://raw.githubusercontent.com/jdecked/twemoji/v17.0.3/";
        public const string LicenseFile = "LICENSE-GRAPHICS";
        public const string LicenseSha256 = "8ae9438818c26e4873b91d8c6ad620526c011e27e125677f13031eda903f007c";

        public static readonly EmojiEntry[] All = new EmojiEntry[]
        {
            new EmojiEntry("Smileys", "\uD83D\uDE00", "1f600", "9cfc5ad34e89b6eebddcd5ec715c224a86c99ea5b9fad999407dee7e32f681b6"),
            new EmojiEntry("Smileys", "\uD83D\uDE03", "1f603", "771063f3b2b4950334f4d187069c87041036842149b3e4268221db325b6bde0e"),
            new EmojiEntry("Smileys", "\uD83D\uDE04", "1f604", "ae89e05450587e8f1f4786c0d266cf94ef2b306782fb6f9c30e43c0023ed148b"),
            new EmojiEntry("Smileys", "\uD83D\uDE01", "1f601", "ed2d1ae8dcdaf6a12e9af7d7bced25db2c3e8f38e624f930d13ff697e3285e7b"),
            new EmojiEntry("Smileys", "\uD83D\uDE06", "1f606", "0d46390df7858374bd26a539915193d11a6bfb5fef11ff8e7572a1d32851b126"),
            new EmojiEntry("Smileys", "\uD83D\uDE05", "1f605", "723bceeff120314bc0b84d09adc1fa250a25361d04ff700964fdee58819b20cd"),
            new EmojiEntry("Smileys", "\uD83E\uDD23", "1f923", "4a371ef1123fee52185f3098d3d3b65dc7f695fdd574ea977ea2afedff963ad5"),
            new EmojiEntry("Smileys", "\uD83D\uDE02", "1f602", "c252a58367211c11d839155e50dc5e98551826c64b8d2e8d6267124c054ceae0"),
            new EmojiEntry("Smileys", "\uD83D\uDE42", "1f642", "c7a2c052f383509ac9ec9da7f34cccc4c1d35040799426588c54a0d83cd9628f"),
            new EmojiEntry("Smileys", "\uD83D\uDE09", "1f609", "f6b86554472159b74d0ab91b2cb3cd75cf361ccf96a141a4ebd3363c38e7f8eb"),
            new EmojiEntry("Smileys", "\uD83D\uDE0A", "1f60a", "5dc76062acc5a80d95fdd823db908640d52dac05ad1a4899ab9e723bfa0bbbfb"),
            new EmojiEntry("Smileys", "\uD83D\uDE07", "1f607", "872aa7cb9054653cf230e04ce27232d9322b3f93c911e7ee8e7fe4d19bce50e6"),
            new EmojiEntry("Smileys", "\uD83E\uDD70", "1f970", "d1b268344fa3a7ee27d6efbbcabd6d4f5214294120de1c2c85b6b0979e22e9ab"),
            new EmojiEntry("Smileys", "\uD83D\uDE0D", "1f60d", "ed402544b4e4f781f41f314388826889618fdb9972b28212dcdf91c76673ac0c"),
            new EmojiEntry("Smileys", "\uD83E\uDD29", "1f929", "494a5d33fa83756c26384a2e7a90142bde9d8d0b4f81c7f2721b9527cf5ce684"),
            new EmojiEntry("Smileys", "\uD83D\uDE18", "1f618", "cfed922412955d0a799efd8a55c27bd26423db41334d429ce3e037d726545a22"),
            new EmojiEntry("Smileys", "\uD83D\uDE0B", "1f60b", "d8cb622d4c4edfeebcfed51dfc1dd883c5e5ec35e93292f976b6575ee31c8f62"),
            new EmojiEntry("Smileys", "\uD83D\uDE1B", "1f61b", "3163a5e255227b243910497d540132fb6658b673e5dce9a6039c26309d51eb0f"),
            new EmojiEntry("Smileys", "\uD83D\uDE1C", "1f61c", "6a693010b77b3ab855dfa10f04416f7bd49775f13cb069c88c7c460e060e69d2"),
            new EmojiEntry("Smileys", "\uD83E\uDD2A", "1f92a", "d62c07b3176f768fde3a6b181fef0780a20ca6cc54d462f0956528056fd4ac7b"),
            new EmojiEntry("Smileys", "\uD83E\uDD17", "1f917", "75051001faed2bddddb6c9e67ee6b62f4f6e72395d2effcaa017bd4e6970b29f"),
            new EmojiEntry("Smileys", "\uD83E\uDD2D", "1f92d", "30fbcc1105112de4535af5034c72d2bec5c273d909209959b96a4d3bda3551c2"),
            new EmojiEntry("Smileys", "\uD83E\uDD2B", "1f92b", "fc824cf580decba036c7cdfb9c93190e8a18554f3e825479783da92e294217af"),
            new EmojiEntry("Smileys", "\uD83E\uDD14", "1f914", "5116f7d07677f06785887c0af23c189b541a306d6b792d605ffaf3ed9f0e912d"),
            new EmojiEntry("Smileys", "\uD83D\uDE10", "1f610", "fa129602e5025a314b309f2dff231dfd8462624b6e398852bc3992a5c2b46585"),
            new EmojiEntry("Smileys", "\uD83D\uDE11", "1f611", "42b8a5b58df75bc1294b95fba7fd4ee7972145086c03875f780350e26cb0a9c7"),
            new EmojiEntry("Smileys", "\uD83D\uDE36", "1f636", "e14f0ccf56e8c7c123e4da42b3578b18a4d03231de63c592a9a83d038465818e"),
            new EmojiEntry("Smileys", "\uD83D\uDE44", "1f644", "8c5956f266757c1dff82ed1440caaf3c3896972f8983f1310fca911898225d3d"),
            new EmojiEntry("Smileys", "\uD83D\uDE0F", "1f60f", "47fea9c90bfea7bd4990f004d7018f5c304d7423559a5ac2001caab666252d66"),
            new EmojiEntry("Smileys", "\uD83D\uDE2C", "1f62c", "9aa4a3e9dfe5a3a01d1a0036a86749f887cd2b0f5dc7238b5cbefff180f13853"),
            new EmojiEntry("Smileys", "\uD83D\uDE34", "1f634", "2aeea8badccceed72027d37081ac75d81ced2932c382ed89527d427585081df7"),
            new EmojiEntry("Smileys", "\uD83D\uDE37", "1f637", "718f1b67b62e24331966b2e1c7e8b569f317b11dd05e36c693093ed1dd812a79"),
            new EmojiEntry("Smileys", "\uD83E\uDD73", "1f973", "71fd857dc56ed5075127c33ccdae59ad5d01f1fdb60c594d96bb84cab16c305c"),
            new EmojiEntry("Smileys", "\uD83D\uDE0E", "1f60e", "0e96db4df869e8e4a38a76b7be66ebb1d80af768e193fcbb7e29abde3980af2a"),
            new EmojiEntry("Smileys", "\uD83E\uDD13", "1f913", "bf5a4edd87080ae87fe86ed060de3597f7cb7380bab93896021c582f8838f482"),
            new EmojiEntry("Smileys", "\uD83E\uDDD0", "1f9d0", "36db3512ea89976cd734e544a1edd6a0609a824da59b596146f955cb6274040c"),
            new EmojiEntry("Smileys", "\uD83D\uDE15", "1f615", "5dc1ca61bbc8cd4bf02bc0d192e798628af5c2b64f08d0179d7a828a9527ec33"),
            new EmojiEntry("Smileys", "\uD83D\uDE1F", "1f61f", "0fbb9ec4c4fd72374806a194e031c2f0cae4e5f91ad4b911ad1e12f0e5832fb9"),
            new EmojiEntry("Smileys", "\uD83D\uDE41", "1f641", "62519e66efce1c4eda4bf0d9f5ef691270f58a2baff0794d8f01c82dd0ac19af"),
            new EmojiEntry("Smileys", "\uD83D\uDE2E", "1f62e", "0fc5cd0fa11b6a4acdc3a7bac6d3f6118169a00f26434a5143becef960b3010d"),
            new EmojiEntry("Smileys", "\uD83D\uDE32", "1f632", "488c8e360097e4d5240e3cc712abb1e6108eabac2aa37cf261444cdd83c1970a"),
            new EmojiEntry("Smileys", "\uD83D\uDE33", "1f633", "7356090df6d9c7a423495be295b2ceca90afeb0e6c2805e03dbd4b01d1753191"),
            new EmojiEntry("Smileys", "\uD83E\uDD7A", "1f97a", "2b35a39ad1aab82798f1a6c9c525b60b4226c57c2238b62e1052a8b92976a9fe"),
            new EmojiEntry("Smileys", "\uD83D\uDE22", "1f622", "077de4b040aec6cc02d33b34881425cb6576e677da9dbaa6010706d4010b6e65"),
            new EmojiEntry("Smileys", "\uD83D\uDE2D", "1f62d", "c22c89c24607d04f39094af2216b611d1d033055b31afafbc6a185990982b844"),
            new EmojiEntry("Smileys", "\uD83D\uDE31", "1f631", "42e6b438eba01cf441e8e1bdde37ddb2bd9278ac26526acbb930c1557a75e0b7"),
            new EmojiEntry("Smileys", "\uD83D\uDE24", "1f624", "d30ccefefb790a20a4f3030099b9b8f0eac8247eb163e338080615d7381072a4"),
            new EmojiEntry("Smileys", "\uD83D\uDE21", "1f621", "f65d755195cfb95f5c3e38a33d5ccd935236ff2869a720cdbcb8ac667c073e7e"),
            new EmojiEntry("Smileys", "\uD83E\uDD2C", "1f92c", "5ab79de1ba89b1176e07530b48c5864cb80914cc806c3126068f9cc76061d7f3"),
            new EmojiEntry("Smileys", "\uD83D\uDE08", "1f608", "13f3065da3da99b36f3035fa3bd7285197ff0e6d754b099726b94f471ed37c51"),
            new EmojiEntry("Smileys", "\uD83D\uDC80", "1f480", "9fd5d570065aa68949bb713242f3915938d5bbaf7e24fe319b0553214c63e085"),
            new EmojiEntry("Smileys", "\uD83D\uDCA9", "1f4a9", "91f689597621d1bed4653fb69281128d46ac44cca08248e4cef1ef7f9a46b724"),
            new EmojiEntry("Smileys", "\uD83E\uDD21", "1f921", "3dccb64c1d5291548d5ed9df446b613c663a1ce35442826d42535b5ba629b03b"),
            new EmojiEntry("Smileys", "\uD83D\uDC7B", "1f47b", "0bbefaaa534514982fadf95e38c2e3c038cba69d004081bbc4347ca17b7055a9"),
            new EmojiEntry("Smileys", "\uD83D\uDC7D", "1f47d", "98512d55856ab96f90794ef4d978ddedfa6b5ef130cbd70273ba12f54e3fa0f0"),
            new EmojiEntry("Smileys", "\uD83E\uDD16", "1f916", "3ee07ef63a271f85d7ae80d9f1a70b021cd1d31df387c603fa24c8f0ee7f8b2f"),
            new EmojiEntry("People", "\uD83D\uDC4D", "1f44d", "42b43325b3edacba2a0e72b742bdc6fc5e4bc2ad38adca271fcc6d8353639887"),
            new EmojiEntry("People", "\uD83D\uDC4E", "1f44e", "1526f8c1b76ffbec594a779f1afdc1850695ae0c3f8a48ce11b2cd788634fbae"),
            new EmojiEntry("People", "\uD83D\uDC4C", "1f44c", "0b94da70454fffda13f6062f51a6c4772c8854be2a963da23b4298c5fb11ef56"),
            new EmojiEntry("People", "\u270C\uFE0F", "270c", "cea523470d65f2a2a77529df0ba9dbd8b6d625e0311c3438a5a30b4dc583235d"),
            new EmojiEntry("People", "\uD83E\uDD1E", "1f91e", "a6cacfd48ecadc57d1364937d78d96b8909afca8bb8de358cfc3adaba38250df"),
            new EmojiEntry("People", "\uD83E\uDD1F", "1f91f", "fb59a07586c7aa5e1afd66f523d3aa9074a431b0d0b86dca34e36a110a22133e"),
            new EmojiEntry("People", "\uD83E\uDD18", "1f918", "13da23e323658067823edcbc9f6033522a57cbe4325eb72470ab93f6c77f5c38"),
            new EmojiEntry("People", "\uD83D\uDC4F", "1f44f", "876e139116fc16aa3c4d125fc455be61e9c68bf474539ca822a2d2edee6a7459"),
            new EmojiEntry("People", "\uD83D\uDE4C", "1f64c", "ae6858c878b323f91c25b2d02813a135c3942294fc44aafc643fde03f7c24d00"),
            new EmojiEntry("People", "\uD83D\uDE4F", "1f64f", "697cae8fe5f354b203da580b96c35e11316f327d8beeeb7ee5fdc5322e67f9f4"),
            new EmojiEntry("People", "\uD83D\uDCAA", "1f4aa", "a90ba12ee97053126082be7521250b49fd75717fce6acff03bd000138234f224"),
            new EmojiEntry("People", "\uD83D\uDC4B", "1f44b", "dfee1561c6e59c90f7a292f90157bae85f75ccb3ae27b655898a51429e3a8910"),
            new EmojiEntry("People", "\uD83E\uDD1D", "1f91d", "32bdc6cd0c499c35f06893f27f0a2104abc2fd47ffb9f57ca1fbac92b4badcc1"),
            new EmojiEntry("People", "\uD83D\uDC40", "1f440", "487739c941203283fc25b1bac02b4b8f3d59672e3dec2154f575060206bbb86a"),
            new EmojiEntry("People", "\uD83E\uDEE1", "1fae1", "7090139cd3138d0701f986f2c540964ebecdb74202f181efcc632cb37610a400"),
            new EmojiEntry("People", "\uD83E\uDD37", "1f937", "eb91d0614bad6e392cf738c2f061d2f690e0f7d037065761b8c888cd173a1add"),
            new EmojiEntry("People", "\uD83E\uDD26", "1f926", "b5ae2ae7bd5c4bb848494cb586842c9f69bbfab645ecfd85c0c912944c90bf09"),
            new EmojiEntry("People", "\uD83D\uDE4B", "1f64b", "8525ed1115d1d80e836ab9afff838cad0c2d236bc65ac85718e9553842c59cdf"),
            new EmojiEntry("Hearts", "\u2764\uFE0F", "2764", "68da7c6dc7d9c0456174f2575abe8f8abd52cde7a4017700579519173a8a4a34"),
            new EmojiEntry("Hearts", "\uD83E\uDDE1", "1f9e1", "a5fa48e7294ba0402926228c0593d13d7137809c3b2f40e795f3ac2e3f10e85a"),
            new EmojiEntry("Hearts", "\uD83D\uDC9B", "1f49b", "f868eae088efc7423d0e369ed7a6dc0c47d2014795ae356ba4e49c671bbd4434"),
            new EmojiEntry("Hearts", "\uD83D\uDC9A", "1f49a", "3bf853fc84828d4ad0543b5cfba0e0dc0953333fae7d52b30406fe710276fd2b"),
            new EmojiEntry("Hearts", "\uD83D\uDC99", "1f499", "1a8a8c6fb5cb9e2a357dcd423809406b865411724a484e826a44023442410bd7"),
            new EmojiEntry("Hearts", "\uD83D\uDC9C", "1f49c", "d5f855edcab4a6c68167a0f22e3d37ebeedc5146d01abca12659c5e366a98eb1"),
            new EmojiEntry("Hearts", "\uD83D\uDDA4", "1f5a4", "adf8f7516c8fd60f677363970554b761c426afa9c1f59ce675f8f8de2d5d264c"),
            new EmojiEntry("Hearts", "\uD83E\uDD0D", "1f90d", "958bbff67b55f57f6ed86ab281a02523ec9191cbd20dfc5f5f76092ba092aae6"),
            new EmojiEntry("Hearts", "\uD83D\uDC94", "1f494", "bc6c9312cc71086497645902e60417a4a1248ad14243fec6cb53bbd5982db93d"),
            new EmojiEntry("Hearts", "\uD83D\uDC95", "1f495", "e906fa70cad35b6894aae18ea86f100621a70c0a2bc6fbad556595ec26c258bd"),
            new EmojiEntry("Hearts", "\uD83D\uDCAF", "1f4af", "83eb1caeac0dcb2b8c60d92fd7aebf29aa61da109afb49bd5be294128e762da4"),
            new EmojiEntry("Hearts", "\uD83D\uDCA5", "1f4a5", "c64d226a7d1d5a7e4b5d9f4d485ee8261082044eb39b778b9d1e56b40c561632"),
            new EmojiEntry("Hearts", "\uD83D\uDD25", "1f525", "b0f4c358afcce0ddcde029e72ea2d6054eece0ce5a34c9a7e0c5761ff4f33a25"),
            new EmojiEntry("Hearts", "\u2728", "2728", "ad3afa9c7beee612fdc68691d25b36dda934204a3d8916ff3d14df83548a0f84"),
            new EmojiEntry("Hearts", "\u2B50", "2b50", "427b7f8fbe46ce254c9950b28e5a370353c27d1f67d400c88c333a8f0ac3e599"),
            new EmojiEntry("Hearts", "\uD83C\uDF89", "1f389", "7289a4aa98f9a81a1108f98abfcebd214dab96df64c8ea67ed6e83116a8e4ce8"),
            new EmojiEntry("Hearts", "\uD83C\uDF8A", "1f38a", "40dbfba72ab3e8867238ee20a63b9032f4a9e8c2fd8d195ee37a9055bddf05c5"),
            new EmojiEntry("Activities", "\uD83C\uDFAE", "1f3ae", "f5083b4905181260208a00a6f7a45947cffc9d66a3fc2295574f28498dff20c5"),
            new EmojiEntry("Activities", "\uD83D\uDD79\uFE0F", "1f579", "2633773704d4ed69129090dec201161b0c9373615cb2d9e2fce8b205f79a3eb3"),
            new EmojiEntry("Activities", "\uD83C\uDFA7", "1f3a7", "96e389bc7e931917946bab3b7f6cb92a9949b7c13386c458f032b53602b0b69a"),
            new EmojiEntry("Activities", "\uD83C\uDFA4", "1f3a4", "f90956be5bea11d8a25e15f8b6280cf976f836b648648b20dec1148f824e632b"),
            new EmojiEntry("Activities", "\uD83C\uDFB5", "1f3b5", "a2bb3effc83f9edb92d54a9ad69cd84bbeff44df1150f44537897fdca16c6c31"),
            new EmojiEntry("Activities", "\uD83C\uDFB6", "1f3b6", "1d14eeb2fd2bd3648cd6afd9dec466ca3dd76dd5a026a2eb16844392ef3784d9"),
            new EmojiEntry("Activities", "\uD83C\uDFAC", "1f3ac", "0952427c6f4fa6f960b8954afbf10c45ab099876ec25e748b73ade0757e88207"),
            new EmojiEntry("Activities", "\uD83D\uDCFA", "1f4fa", "6d3156bb4767202a6f0d6af67ca81c3c058475c8159dd77c7538988c25438596"),
            new EmojiEntry("Activities", "\uD83C\uDFC6", "1f3c6", "926ce5a398578129b6aae74a837809c7ead80dae6714c7b07ee90d8463b7479a"),
            new EmojiEntry("Activities", "\uD83E\uDD47", "1f947", "41ea4ddf7822fcd58d0147d394824cd634e02da6f3f72fcbdbe14b1178051e59"),
            new EmojiEntry("Activities", "\u26BD", "26bd", "325f22f082d877326c2920368c869974f59eabee083b21c8f96bb995ce6c6acb"),
            new EmojiEntry("Activities", "\uD83C\uDFC0", "1f3c0", "9fc51addbd8ea22a2bcd317359948ebe206cc14fab44bbffe769e72de9b07c6f"),
            new EmojiEntry("Activities", "\uD83C\uDFB2", "1f3b2", "71a92260bbe0b8dc08285f1357c8fe6c6cd8b344dafc75391503f2dc3a2270c1"),
            new EmojiEntry("Activities", "\u265F\uFE0F", "265f", "7f146900985c46e22137ff4088b71899d8dbcd383ce8d93006eff4e3f3e4beb0"),
            new EmojiEntry("Food", "\uD83C\uDF55", "1f355", "26e001fabb2015c16ff79d93574c8ec0597892d4818cc3beb4551b1fe0a47caf"),
            new EmojiEntry("Food", "\uD83C\uDF54", "1f354", "dd5bba9e0fb4a3f1484ff02a081ead0dc84262c81c56168ce81f63576d41b357"),
            new EmojiEntry("Food", "\uD83C\uDF5F", "1f35f", "7305e5217a346aace5431ca7d4f064861e7af3b0b0340ad5c320b7c1e0bd6be3"),
            new EmojiEntry("Food", "\uD83C\uDF2E", "1f32e", "887db4098438d1f2be43c7065a03a2ded9f19258c603bfeb3b3618f13e24fcd3"),
            new EmojiEntry("Food", "\uD83C\uDF7F", "1f37f", "68cf3f400d769098d5d06cd0b304e12695d19e96530e0e8237bcef1213d50e3d"),
            new EmojiEntry("Food", "\uD83C\uDF69", "1f369", "7e373664a6c17b957fa0abf2a48a9c23ef235886bb9209f76f0639ab792a16d7"),
            new EmojiEntry("Food", "\uD83C\uDF6A", "1f36a", "fb8416c661fbb284f286b7154a98cbcd6451c27c27ef0c9f588f57812936c096"),
            new EmojiEntry("Food", "\uD83C\uDF82", "1f382", "8435f19559eaa852af385c4c2efadbbeeeaee4b325ecfd96c54bf4e7ba0f00c5"),
            new EmojiEntry("Food", "\u2615", "2615", "7698fb9dcbaf4459b3e29ee97eba2df94c0adf2daf118c91dbfeb3e569d470ab"),
            new EmojiEntry("Food", "\uD83C\uDF7A", "1f37a", "427b09785b1747753cc59fde17cc5db5c5f073e9be64d9ae40d5203fb0523f68"),
            new EmojiEntry("Food", "\uD83C\uDF7B", "1f37b", "1d0082ace3c56889c7dff14ac3c617192c6a3fba6cba3b2c7823348f55d6710a"),
            new EmojiEntry("Food", "\uD83E\uDD42", "1f942", "7b18bd04b0555d15f71c21198a8205ece39b14879212a1bc057fb97312a2e2ea"),
            new EmojiEntry("Food", "\uD83C\uDF77", "1f377", "11a4463036a0b0a1f34b00bd13394e677a26b423cbf42114982030d9cf966215"),
            new EmojiEntry("Food", "\uD83E\uDDC3", "1f9c3", "c356cc8b9375c6cc650e0aeb27e4d5c55be719580d692899b506691adbc1c687"),
            new EmojiEntry("Things", "\uD83D\uDE80", "1f680", "a4d8b439357e38510505048dfc85c54c9cbb08c04ef4d9d4bacd03572a17c9a5"),
            new EmojiEntry("Things", "\uD83D\uDCA1", "1f4a1", "bf0b74aa708457db96c21f658f0eff5ec185a10242f4aa157a86823149c7d704"),
            new EmojiEntry("Things", "\uD83D\uDCCC", "1f4cc", "62539521ce2183a0282127a792af6af4ecab5b4dc33f6f2d87f7681ab08628ef"),
            new EmojiEntry("Things", "\uD83D\uDCCE", "1f4ce", "6930b0a8c19fbbfbb3f82f2ee639b86e863a7bc0dabe3d03197e80bb0541a0c5"),
            new EmojiEntry("Things", "\uD83D\uDD14", "1f514", "2256b6c50560132ea3b72c7453527acefd8423e7afff830d350e366bd3523e72"),
            new EmojiEntry("Things", "\uD83D\uDD07", "1f507", "ad03599599ea461a1791d2394d0cc7c0faff6b41e7ab9826eca04fa11f34d8d9"),
            new EmojiEntry("Things", "\uD83C\uDF81", "1f381", "1d8142ccc2d696dd6571ff277ec26b81d4231e1e00377aebaa5270a5f37075d5"),
            new EmojiEntry("Things", "\uD83D\uDCA4", "1f4a4", "9e2b51deb7f8dcb594c034b7510c2373ace9e9f52420aed023b8c0bfef60d1c6"),
            new EmojiEntry("Things", "\u2705", "2705", "e3cc2f7251c41ff1f4b2e07a3ccd074d21288160fbd9893f0f0e4fc62d2c63c5"),
            new EmojiEntry("Things", "\u274C", "274c", "3dc3ebd43d1d85af678ac5a72b0305df7bcd20b621f95291d76b827f5e47d89f"),
            new EmojiEntry("Things", "\u26A0\uFE0F", "26a0", "7a03a74a92cb2f04b7f3e0338f51a3c4dfc1491a8f046b722f8a951502a7740e"),
            new EmojiEntry("Things", "\u2753", "2753", "ac1daab1db4fbeec9105c359a5d58fb8519b27eb2fc8f6644ef97cf74bc2cdac"),
            new EmojiEntry("Things", "\u2757", "2757", "2f35fc344d8e771b2bdbb7802f20e354f0a9c9007ea2fe6a8cc62d6b62391b32"),
            new EmojiEntry("Things", "\u2795", "2795", "f76f0ea18167458d10ab7315e0df8fd9bf843abf629e37529bbd8590db89ea7c"),
            new EmojiEntry("Things", "\uD83D\uDC51", "1f451", "086f008b14ecc4498583235dc97b2619bda6dfc3a5b221d837c0a7306d1d0115"),
            new EmojiEntry("Things", "\uD83D\uDC8E", "1f48e", "3764291f25fec57d7dd404c33015a1deb5e553a8e3b7267e40ca0a92e39dd5f8"),
            new EmojiEntry("Things", "\uD83C\uDF19", "1f319", "3a9ada9e16379eb0bf8f470b439dfd62fbdffa694c18d1fc44a251a8479e768b"),
            new EmojiEntry("Things", "\u2600\uFE0F", "2600", "b39314735b047ac70961be0a361e4335a076cb3cc8afd22ab720ec22e88a9d0d"),
            new EmojiEntry("Things", "\uD83C\uDF08", "1f308", "90088dfdd587816083890ceb1b25fa1923820406f0f9ceee00006f9899ef3a32"),
            new EmojiEntry("Things", "\u26A1", "26a1", "954e52c6f49987daf74ad9341c55874ada731def069dd938842f170022aa2fc8"),
        };

        public static readonly string[] Groups = { "Smileys", "People", "Hearts", "Activities", "Food", "Things" };

        private static readonly Dictionary<string, EmojiEntry> ByText = BuildIndex();
        private static readonly Dictionary<string, Image> Cache = new Dictionary<string, Image>(StringComparer.Ordinal);
        private static readonly object CacheLock = new object();
        private static volatile bool _verified;

        private static Dictionary<string, EmojiEntry> BuildIndex()
        {
            Dictionary<string, EmojiEntry> index = new Dictionary<string, EmojiEntry>(StringComparer.Ordinal);
            int i;
            for (i = 0; i < All.Length; i++)
            {
                index[All[i].Text] = All[i];
                string bare = All[i].Text.Replace("️", String.Empty);
                if (!index.ContainsKey(bare))
                    index[bare] = All[i];
            }
            return index;
        }

        /// <summary>True once the installed pack has been verified this session.</summary>
        public static bool Available { get { return _verified; } }

        public static string Folder
        {
            get { return Path.Combine(AssetStore.Root, "twemoji-" + Version); }
        }

        public static EmojiEntry Find(string text)
        {
            if (String.IsNullOrEmpty(text))
                return null;
            EmojiEntry entry;
            if (ByText.TryGetValue(text, out entry))
                return entry;
            return ByText.TryGetValue(text.Replace("️", String.Empty), out entry) ? entry : null;
        }

        /// <summary>
        /// Splits text into plain runs and emoji this pack can draw in colour.
        /// Each returned item is either an EmojiEntry or a string.
        /// </summary>
        public static List<object> Tokenize(string text)
        {
            List<object> parts = new List<object>();
            if (String.IsNullOrEmpty(text))
                return parts;
            StringBuilder plain = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                EmojiEntry match = null;
                int length = 0;
                // Longest match first: up to 3 UTF-16 units (surrogate pair + FE0F).
                int tryLength;
                for (tryLength = Math.Min(3, text.Length - i); tryLength >= 1 && match == null; tryLength--)
                {
                    EmojiEntry candidate;
                    if (ByText.TryGetValue(text.Substring(i, tryLength), out candidate))
                    {
                        match = candidate;
                        length = tryLength;
                    }
                }
                if (match != null)
                {
                    if (i + length < text.Length && text[i + length] == '️')
                        length++;
                    if (plain.Length > 0)
                    {
                        parts.Add(plain.ToString());
                        plain.Length = 0;
                    }
                    parts.Add(match);
                    i += length;
                }
                else
                {
                    plain.Append(text[i]);
                    i++;
                }
            }
            if (plain.Length > 0)
                parts.Add(plain.ToString());
            return parts;
        }

        /// <summary>The colour image for an emoji, or null if the pack isn't installed and verified.</summary>
        public static Image GetImage(EmojiEntry entry)
        {
            if (entry == null || !_verified)
                return null;
            lock (CacheLock)
            {
                Image image;
                if (Cache.TryGetValue(entry.FileName, out image))
                    return image;
                try
                {
                    byte[] bytes = File.ReadAllBytes(Path.Combine(Folder, entry.FileName + ".png"));
                    if (!AssetStore.HashMatches(bytes, entry.Sha256))
                        return null; // Changed on disk since verification: don't trust it.
                    image = Image.FromStream(new MemoryStream(bytes));
                }
                catch
                {
                    image = null;
                }
                Cache[entry.FileName] = image;
                return image;
            }
        }

        public static List<AssetFile> Files()
        {
            List<AssetFile> files = new List<AssetFile>();
            int i;
            for (i = 0; i < All.Length; i++)
                files.Add(new AssetFile(All[i].FileName + ".png", BaseUrl + "assets/72x72/" + All[i].FileName + ".png", All[i].Sha256));
            files.Add(new AssetFile(LicenseFile + ".txt", BaseUrl + LicenseFile, LicenseSha256));
            return files;
        }

        /// <summary>Checks the installed files. Cheap (about 120 KB of hashing).</summary>
        public static bool Verify()
        {
            _verified = AssetStore.VerifyFolder(Folder, Files());
            if (!_verified)
            {
                lock (CacheLock)
                    Cache.Clear();
            }
            return _verified;
        }

        /// <summary>Downloads, verifies and installs the pack. Throws with a readable message on failure.</summary>
        public static void Install(Action<int, int> progress, Func<bool> cancelled)
        {
            AssetStore.InstallFolder(Folder, Files(), progress, cancelled);
            AssetStore.RemoveOtherVersions("twemoji-", "twemoji-" + Version);
            Verify();
        }
    }

    internal sealed class AssetFile
    {
        public readonly string Name;
        public readonly string Url;
        public readonly string Sha256;

        public AssetFile(string name, string url, string sha256)
        {
            Name = name;
            Url = url;
            Sha256 = sha256;
        }
    }

    /// <summary>Verified downloads into %LocalAppData%\Maela\assets.</summary>
    internal static class AssetStore
    {
        /// <summary>Only these official sources may be downloaded from.</summary>
        private static readonly string[] AllowedPrefixes = { "https://raw.githubusercontent.com/jdecked/twemoji/" };
        private const int MaxFileBytes = 2 * 1024 * 1024;

        /// <summary>Tests only: install somewhere else.</summary>
        internal static string RootOverrideForTests { get; set; }

        public static string Root
        {
            get
            {
                if (!String.IsNullOrEmpty(RootOverrideForTests))
                    return RootOverrideForTests;
                return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maela"), "assets");
            }
        }

        public static bool HashMatches(byte[] data, string expectedSha256)
        {
            if (data == null || String.IsNullOrEmpty(expectedSha256))
                return false; // No pinned hash, no trust.
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                StringBuilder hex = new StringBuilder(64);
                int i;
                for (i = 0; i < hash.Length; i++)
                    hex.Append(hash[i].ToString("x2"));
                return String.Equals(hex.ToString(), expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static bool VerifyFolder(string folder, IList<AssetFile> files)
        {
            try
            {
                if (!Directory.Exists(folder))
                    return false;
                int i;
                for (i = 0; i < files.Count; i++)
                {
                    string path = Path.Combine(folder, files[i].Name);
                    if (!File.Exists(path) || !HashMatches(File.ReadAllBytes(path), files[i].Sha256))
                        return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Downloads every file into a staging folder, verifying each one, then swaps the staging
        /// folder into place. Nothing is installed unless every file verifies.
        /// </summary>
        public static void InstallFolder(string folder, IList<AssetFile> files, Action<int, int> progress, Func<bool> cancelled)
        {
            string staging = folder + ".download";
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072; // TLS 1.2
            int i;
            for (i = 0; i < files.Count; i++)
            {
                if (cancelled != null && cancelled())
                {
                    TryDeleteFolder(staging);
                    throw new OperationCanceledException("Download cancelled.");
                }
                AssetFile file = files[i];
                if (!IsAllowed(file.Url))
                    throw new InvalidOperationException("Refusing to download from an unapproved source: " + file.Url);
                byte[] data = Download(file.Url);
                if (!HashMatches(data, file.Sha256))
                {
                    TryDeleteFolder(staging);
                    throw new InvalidOperationException("A downloaded file (" + file.Name + ") failed its integrity check, so nothing was installed.");
                }
                File.WriteAllBytes(Path.Combine(staging, file.Name), data);
                if (progress != null)
                    progress(i + 1, files.Count);
            }

            string backup = folder + ".old";
            TryDeleteFolder(backup);
            if (Directory.Exists(folder))
                Directory.Move(folder, backup);
            try
            {
                Directory.Move(staging, folder);
            }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(folder))
                    Directory.Move(backup, folder);
                throw;
            }
            TryDeleteFolder(backup);
        }

        public static void RemoveOtherVersions(string prefix, string keep)
        {
            try
            {
                if (!Directory.Exists(Root))
                    return;
                foreach (string dir in Directory.GetDirectories(Root, prefix + "*"))
                {
                    if (!String.Equals(Path.GetFileName(dir), keep, StringComparison.OrdinalIgnoreCase))
                        TryDeleteFolder(dir);
                }
            }
            catch { }
        }

        private static bool IsAllowed(string url)
        {
            int i;
            for (i = 0; i < AllowedPrefixes.Length; i++)
            {
                if (url.StartsWith(AllowedPrefixes[i], StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static byte[] Download(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "Maela/" + Updater.CurrentVersionText;
            request.Timeout = 20000;
            request.ReadWriteTimeout = 20000;
            request.AllowAutoRedirect = false; // Official raw URLs never redirect; refuse if one does.
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (MemoryStream memory = new MemoryStream())
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException("Download failed (" + (int)response.StatusCode + "): " + url);
                byte[] buffer = new byte[16384];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    memory.Write(buffer, 0, read);
                    if (memory.Length > MaxFileBytes)
                        throw new InvalidOperationException("Download larger than expected: " + url);
                }
                return memory.ToArray();
            }
        }

        private static void TryDeleteFolder(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch { }
        }
    }
}
