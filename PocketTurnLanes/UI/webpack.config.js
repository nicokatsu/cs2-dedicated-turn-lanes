const path = require("path");
const webpack = require("webpack");
const SOURCE_MOD = require("./mod.json");
const MiniCssExtractPlugin = require("mini-css-extract-plugin");
const { CSSPresencePlugin } = require("./tools/css-presence");
const TerserPlugin = require("terser-webpack-plugin");
const gray = (text) => `\x1b[90m${text}\x1b[0m`;

const CSII_USERDATAPATH = process.env.CSII_USERDATAPATH;
const CHANNELS = {
  Stable: {
    id: "DedicatedTurnLanes",
    displayName: "Dedicated Turn Lanes",
  },
  Alpha: {
    id: "DedicatedTurnLanesAlpha",
    displayName: "Dedicated Turn Lanes Alpha",
  },
  Dev: {
    id: "DedicatedTurnLanesDev",
    displayName: "Dedicated Turn Lanes Dev",
  },
};

if (!CSII_USERDATAPATH) {
  throw "CSII_USERDATAPATH environment variable is not set, ensure the CSII Modding Toolchain is installed correctly";
}

const resolveMod = (env = {}) => {
  const channel = env.modChannel || process.env.MOD_CHANNEL || "Stable";
  const channelDefaults = CHANNELS[channel];

  if (!channelDefaults) {
    throw new Error(
      `Unsupported mod channel '${channel}'. Use 'Stable' or 'Alpha'.`
    );
  }

  return {
    ...SOURCE_MOD,
    ...channelDefaults,
    channel,
    id: env.modId || process.env.MOD_ID || channelDefaults.id,
    displayName:
      env.modDisplayName ||
      process.env.MOD_DISPLAY_NAME ||
      channelDefaults.displayName,
  };
};

const createManifestPlugin = (mod) => ({
  apply(compiler) {
    const { channel, ...manifest } = mod;

    compiler.hooks.compilation.tap("ModManifestPlugin", (compilation) => {
      compilation.hooks.processAssets.tap(
        {
          name: "ModManifestPlugin",
          stage: compilation.PROCESS_ASSETS_STAGE_ADDITIONS,
        },
        () => {
          compilation.emitAsset(
            "mod.json",
            new webpack.sources.RawSource(
              `${JSON.stringify(manifest, null, 2)}\n`
            )
          );
        }
      );
    });
  },
});

module.exports = (env = {}) => {
  const MOD = resolveMod(env);
  const OUTPUT_DIR = `${CSII_USERDATAPATH}\\Mods\\${MOD.id}`;
  const banner = `
 * Cities: Skylines II UI Module
 *
 * Id: ${MOD.id}
 * Channel: ${MOD.channel}
 * Author: ${MOD.author}
 * Version: ${MOD.version}
 * Dependencies: ${MOD.dependencies.join(",")}
`;

  return {
    mode: "production",
    stats: "none",
    entry: {
      [MOD.id]: "./src/index.tsx",
    },
    externalsType: "window",
    externals: {
      react: "React",
      "react-dom": "ReactDOM",
      "cs2/modding": "cs2/modding",
      "cs2/api": "cs2/api",
      "cs2/bindings": "cs2/bindings",
      "cs2/l10n": "cs2/l10n",
      "cs2/ui": "cs2/ui",
      "cs2/input": "cs2/input",
      "cs2/utils": "cs2/utils",
      "cohtml/cohtml": "cohtml/cohtml",
    },
    module: {
      rules: [
        {
          test: /\.tsx?$/,
          use: "ts-loader",
          exclude: /node_modules/,
        },
        {
          test: /\.s?css$/,
          include: path.join(__dirname, "src"),
          use: [
            MiniCssExtractPlugin.loader,
            {
              loader: "css-loader",
              options: {
                url: true,
                importLoaders: 1,
                modules: {
                  auto: true,
                  exportLocalsConvention: "camelCase",
                  localIdentName: "[local]_[hash:base64:3]",
                },
              },
            },
            "sass-loader",
          ],
        },
        {
          test: /\.(png|jpe?g|gif|svg)$/i,
          type: "asset/resource",
          generator: {
            filename: "images/[name][ext][query]",
          },
        },
      ],
    },
    resolve: {
      extensions: [".tsx", ".ts", ".js"],
      modules: ["node_modules", path.join(__dirname, "src")],
      alias: {
        "mod.json": path.resolve(__dirname, "mod.json"),
      },
    },
    output: {
      path: path.resolve(__dirname, OUTPUT_DIR),
      library: {
        type: "module",
      },
      publicPath: "coui://ui-mods/",
    },
    optimization: {
      minimize: true,
      minimizer: [
        new TerserPlugin({
          extractComments: {
            banner: () => banner,
          },
        }),
      ],
    },
    experiments: {
      outputModule: true,
    },
    plugins: [
      new MiniCssExtractPlugin(),
      new CSSPresencePlugin(),
      new webpack.DefinePlugin({
        __MOD_ID__: JSON.stringify(MOD.id),
        __MOD_DISPLAY_NAME__: JSON.stringify(MOD.displayName),
        __MOD_CHANNEL__: JSON.stringify(MOD.channel),
      }),
      createManifestPlugin(MOD),
      {
        apply(compiler) {
          let runCount = 0;
          compiler.hooks.done.tap("AfterDonePlugin", (stats) => {
            console.log(stats.toString({ colors: true }));
            console.log(
              `\n${!runCount++ ? "Built" : "Updated"} ${MOD.id} (${MOD.channel})`
            );
            console.log("   " + gray(OUTPUT_DIR) + "\n");
          });
        },
      },
    ],
  };
};
