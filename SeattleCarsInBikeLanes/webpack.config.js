const path = require('path');

module.exports = {
    entry: {
        bluesky: './wwwroot/src/bskyAuth.ts'
    },
    devtool: 'inline-source-map',
    module: {
        rules: [
            {
                test: /\.ts$/,
                use: 'ts-loader',
                exclude: [
                    '/node_modules/',
                    '/wwwroot/src/bikelanes.ts',
                    '/wwwroot/src/helpers.ts',
                    '/wwwroot/src/index.ts'
                ]
            },
            {
                test: /\.css$/i,
                use: ['style-loader', 'css-loader']
            }
        ]
    },
    resolve: {
        extensions: ['.tsx', '.ts', '.js']
    },
    output: {
        filename: '[name].bundle.js',
        path: path.resolve(__dirname, 'wwwroot/dist'),
        assetModuleFilename: '[name]-[hash][ext][query]',
        clean: true
    },
    watchOptions: {
        ignored: ['**/node_modules']
    }
}
