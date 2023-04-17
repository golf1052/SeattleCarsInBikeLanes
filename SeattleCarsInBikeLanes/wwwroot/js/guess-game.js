let isAdmin = false;
let gameCode = null;
let userId = '';
let username = '';
let map = null;
const seattleBoundingBox = new atlas.data.BoundingBox([-122.436522, 47.495082], [-122.235787, 47.735525]);
const darkMode = window.matchMedia('(prefers-color-scheme: dark)').matches;
let guessLocationDataSource = null;
let realLocationDataSource = null;
let distancesPointDataSource = null;
let distancesLineDataSource = null;
let playersLegendControl = null;
let helpLegendControl = null;
let knownPlayers = [];
let imageLegendControl = null;
let roundTimerInterval = null;
let roundTimeRemaining = null;
let roundAutoStartTimeout = null;
let roundStarted = false;
let lockedIn = false;
let imageFullscreen = false;
let preRoundModal = null;
let disconnectionModal = null;
const popup = new atlas.Popup({
    closeButton: false,
    content: ''
});
let popupTimeout = null;

const connection = new signalR.HubConnectionBuilder()
    .withUrl('/GuessGameHub')
    .withAutomaticReconnect()
    .build();

connection.onreconnecting(error => {
    createDisconnectionModal();
    const title = document.getElementById('disconnectionModalTitle');
    title.innerText = 'Reconnecting';
    const body = document.getElementById('disconnectionModalBody');
    body.innerText = 'Attempting to reconnect';
    disconnectionModal.show();
});

connection.onreconnected(connectionId => {
    createDisconnectionModal();
    disconnectionModal.hide();
});

connection.onclose(error => {
    createDisconnectionModal();
    const title = document.getElementById('disconnectionModalTitle');
    title.innerText = 'Disconnected';
    const body = document.getElementById('disconnectionModalBody');
    body.innerText = 'Refresh the page to create a new game or join an existing game.';
    disconnectionModal.show();
});

connection.on('JoinedGame', function(username) {
    if (playersLegendControl !== null) {
        knownPlayers.push(username);
        updatePlayersLegend();
    }
});

connection.on('ReceiveCountdown', function(type, secondsRemaining) {
    if (type === 'PreRoundTimer') {
        document.getElementById('preRoundModalBody').innerHTML = `<p class="text-center">Next round starts in</p><h1 class="text-center">${secondsRemaining}</h1>`;
        if (!document.getElementById('preRoundModal').classList.contains('show')) {
            if (preRoundModal === null) {
                preRoundModal = new bootstrap.Modal('#preRoundModal');
            }
            preRoundModal.show();
        }
        if (secondsRemaining <= 0 && isAdmin) {
            
            if (roundAutoStartTimeout !== null) {
                clearTimeout(roundAutoStartTimeout);
            }
            connection.invoke('StartRound', gameCode);
        }
    }
});

connection.on('StartedRound', setRoundInfo);

connection.on('ReceiveImage', setRoundImage);

connection.on('AdminLeftGame', function() {
    if (!isAdmin) {
        createDisconnectionModal();
        const title = document.getElementById('disconnectionModalTitle');
        title.innerText = 'Admin disconnected';
        const body = document.getElementById('disconnectionModalBody');
        body.innerText = 'The admin has left and the game has been destroyed. Refresh the page to create a new game or join an existing game.'
        disconnectionModal.show();
    }
});

connection.on('EndRound', function(endRoundInfo) {
    if (roundTimerInterval !== null) {
        clearInterval(roundTimerInterval);
    }

    document.getElementById('lockInButton').setAttribute('hidden', '');
    if (endRoundInfo.gameOver) {
        document.getElementById('roundTimeText').innerText = 'Game over';
    } else {
        document.getElementById('roundTimeText').innerText = 'Round over';
    }
    
    knownPlayers.forEach(player => {
        let updatedPlayer = false;
        for (let i = 0; i < endRoundInfo.scores.length; i++) {
            const score = endRoundInfo.scores[i];
            if (player.username === score.username) {
                updatedPlayer = true;
                player.score += score.score;
                player.lastRoundScore = score.score;
                break;
            }
        }
        if (!updatedPlayer) {
            player.lastRoundScore = 0;
        }
    });

    knownPlayers.sort((a, b) => {
        return b.score - a.score;
    });
    updatePlayersLegend();
    let allFeatures = [];
    const endRoundPosition = new atlas.data.Position(endRoundInfo.longitude, endRoundInfo.latitude);
    const pointFeatures = [];
    const lineFeatures = [];

    pointFeatures.push(new atlas.data.Feature(new atlas.data.Point(endRoundPosition)));
    endRoundInfo.distances.forEach(distance => {
        const distanceInKm = distance.distance / 1000;
        const distanceInMiles = distanceInKm / 1.609344;
        const distanceContent = `${distance.username}\n${distanceInMiles.toFixed(2)} mi (${distanceInKm.toFixed(2)} km)`;
        if (distance.username === username) {
            const guessFeatureCollection = createSinglePointFeatureCollection([distance.longitude, distance.latitude], {
                content: distanceContent
            });
            allFeatures.push(guessFeatureCollection);
            guessLocationDataSource.clear();
            guessLocationDataSource.add(guessFeatureCollection);
        } else {
            pointFeatures.push(
                new atlas.data.Feature(
                    new atlas.data.Point(new atlas.data.Position(distance.longitude, distance.latitude)), {
                    content: distanceContent
                })
            );
        }
        lineFeatures.push(new atlas.data.Feature(new atlas.data.LineString([endRoundPosition, new atlas.data.Position(distance.longitude, distance.latitude)])));
    });

    if (distancesPointDataSource === null) {
        const distancesPointFeatureCollection = new atlas.data.FeatureCollection(pointFeatures);
        distancesPointDataSource = new atlas.source.DataSource(null, { cluster: false });
        distancesPointDataSource.add(distancesPointFeatureCollection);
        map.sources.add(distancesPointDataSource);
        const distancesSymbolLayer = new atlas.layer.SymbolLayer(distancesPointDataSource, null, {
            iconOptions: {
                image: 'marker-yellow'
            },
            textOptions: {
                textField: ['get', 'content']
            }
        });
        map.layers.add(distancesSymbolLayer);
    } else {
        distancesPointDataSource.clear();
        distancesPointDataSource.add(new atlas.data.FeatureCollection(pointFeatures));
    }
    
    if (distancesLineDataSource === null) {
        const distancesLineFeatureCollection = new atlas.data.FeatureCollection(lineFeatures);
        distancesLineDataSource = new atlas.source.DataSource(null, { cluster: false });
        distancesLineDataSource.add(distancesLineFeatureCollection);
        map.sources.add(distancesLineDataSource);
        const distancesLineLayer = new atlas.layer.LineLayer(distancesLineDataSource, null, {
            strokeColor: 'black',
            strokeDashArray: [4, 4]
        });
        map.layers.add(distancesLineLayer);
    } else {
        distancesLineDataSource.clear();
        distancesLineDataSource.add(new atlas.data.FeatureCollection(lineFeatures));
    }
    
    if (realLocationDataSource === null) {
        realLocationDataSource = new atlas.source.DataSource(null, { cluster: false });
        realLocationDataSource.add(createSinglePointFeatureCollection(endRoundPosition));
        map.sources.add(realLocationDataSource);
        const realLocationSymbolLayer = new atlas.layer.SymbolLayer(realLocationDataSource, null, {
            iconOptions: {
                image: 'pin-round-blue',
                offset: [0, 10]
            }
        });
        map.layers.add(realLocationSymbolLayer);
    } else {
        realLocationDataSource.clear();
        realLocationDataSource.add(createSinglePointFeatureCollection(endRoundPosition));
    }

    allFeatures = allFeatures.concat(pointFeatures.concat(lineFeatures));
    const bbox = atlas.data.BoundingBox.fromData(allFeatures);
    const bboxWidth = atlas.data.BoundingBox.getWidth(bbox);
    const bboxHeight = atlas.data.BoundingBox.getHeight(bbox);
    if (bbox !== null) {
        if (bboxWidth < 0.0001 && bboxHeight < 0.0001) {
            map.setCamera({
                center: atlas.data.BoundingBox.getCenter(bbox),
                zoom: 17,
                type: 'fly'
            });
        } else {
            map.setCamera({
                bounds: bbox,
                padding: 50,
                type: 'fly'
            });
        }
    }

    if (!endRoundInfo.gameOver) {
        if (isAdmin) {
            const startRoundButton = document.getElementById('startRoundButton');
            startRoundButton.removeAttribute('disabled');
            startRoundButton.removeAttribute('hidden');
            roundAutoStartTimeout = setTimeout(() => {
                startRoundButton.setAttribute('disabled', '');
                startRoundButton.setAttribute('hidden', '');
                connection.invoke('StartRound', gameCode);
                clearTimeout(roundAutoStartTimeout);
            }, 30000);
        }
    }

    if (endRoundInfo.gameOver) {
        let topScore = 0;
        let topPlayers = [];
        for (let i = 0; i < knownPlayers.length; i++) {
            const knownPlayer = knownPlayers[i];
            if (knownPlayer.score > topScore) {
                topScore = knownPlayer.score;
                topPlayers = [knownPlayer];
            } else if (knownPlayer.score === topScore) {
                topPlayers.push(knownPlayer);
            } else {
                // knownPlayers was sorted before this so we know we can break here
                break;
            }
        }

        const modalTitle = document.getElementById('endGameModalTitle');
        if (topPlayers.length === 1) {
            const winningUsername = knownPlayers[0].username;
            if (winningUsername === username) {
                modalTitle.innerText = 'You won!'
            } else {
                modalTitle.innerText = `${winningUsername} won!`;
            }
        } else {
            let title = '';
            for (let i = 0; i < topPlayers.length; i++) {
                if (i > 0) {
                    if (i + 1 !== topPlayers.length) {
                        title += `, `;
                    } else {
                        title += `, and `;
                    }
                }
                title += topPlayers[i].username;
            }
            title += ` tied for first!`;
            modalTitle.innerText = title;
        }

        knownPlayers.forEach(player => {
            delete player.lastRoundScore;
        });
        document.getElementById('endGameModalBody').innerHTML = buildPlayerList(knownPlayers, false);
        const modal = new bootstrap.Modal('#endGameModal');
        modal.show();
    }
});

document.getElementById('newGameButton').addEventListener('click', function(event) {
    const usernameInput = document.getElementById('usernameInput');
    const numberOfRoundsInput = document.getElementById('numberOfRoundsInput');
    if (usernameInput.value.trim() === '') {
        createInfoPopup(event.target, 'Please enter a username');
        return;
    }
    const numberOfRoundsValue = parseInt(numberOfRoundsInput.value.trim());
    if (isNaN(numberOfRoundsValue)) {
        createInfoPopup(event.target, 'The number of rounds must be a number');
        return;
    }
    if (numberOfRoundsValue < 1 || numberOfRoundsValue > 50) {
        createInfoPopup(event.target, 'The number of rounds must be between 1 and 50');
        return;
    }
    fetch('api/GuessGame/Create', {
        method: 'POST',
        body: JSON.stringify({ rounds: numberOfRoundsValue }),
        headers: {
            'Content-Type': 'application/json'
        }
    })
    .then(response => {
        if (!response.ok) {
            showError('Error when trying to create a new game.');
            throw new Error(`Error when trying to create a new game ${response}`);
        }

        return response.text();
    })
    .then(response => {
        gameCode = response;
        const gameCodeText = document.getElementById('gameCodeText');
        gameCodeText.innerText = `Code: ${gameCode}`;
        gameCodeText.removeAttribute('hidden');
        isAdmin = true;
        username = usernameInput.value.trim();
        createUserId();
        return connection.start();
    })
    .then(() => {
        document.getElementById('newGameContainer').setAttribute('hidden', '');
        document.getElementById('wrapper').removeAttribute('hidden');
        initMap();
        return connection.invoke('AddToGame', gameCode, username);
    });
});

document.getElementById('joinGameButton').addEventListener('click', function(event) {
    const usernameInput = document.getElementById('usernameInput');
    const gameCodeInput = document.getElementById('gameCodeInput');
    if (usernameInput.value.trim() === '') {
        createInfoPopup(event.target, 'Please enter a username');
        return;
    }
    if (gameCodeInput.value.trim() === '' || gameCodeInput.value.trim().length !== 9) {
        createInfoPopup(event.target, 'Game code must be 9 digits');
        return;
    }

    fetch(`api/GuessGame/Join/${gameCodeInput.value.trim()}`)
    .then(response => {
        if (!response.ok) {
            showError('That game does not exist.');
            throw new Error('That game does not exist.');
        }

        gameCode = gameCodeInput.value.trim();
        username = usernameInput.value.trim();
        createUserId();
        return connection.start();
    })
    .then(() => {
        document.getElementById('newGameContainer').setAttribute('hidden', '');
        document.getElementById('wrapper').removeAttribute('hidden');
        initMap();
        return connection.invoke('AddToGame', gameCode, username);
    })
    .catch(() => {});
});

document.getElementById('startGameButton').addEventListener('click', function(event) {
    event.target.setAttribute('disabled', '');
    event.target.setAttribute('hidden', '');
    connection.invoke('StartGame', gameCode)
    .then(() => {
        connection.invoke('StartCountdown', gameCode, 'PreRoundTimer', 3);
    });
});

document.getElementById('startRoundButton').addEventListener('click', function(event) {
    if (roundAutoStartTimeout !== null) {
        clearTimeout(roundAutoStartTimeout);
    }
    event.target.setAttribute('disabled', '');
    event.target.setAttribute('hidden', '');
    clearEndRoundMapItems();
    connection.invoke('StartCountdown', gameCode, 'PreRoundTimer', 3);
});

document.getElementById('shareButton').addEventListener('click', function() {
    const shareButton = document.getElementById('shareButton');
    const shareUrl = `${window.location.origin}${window.location.pathname}?gameCode=${gameCode}`;
    navigator.clipboard.writeText(shareUrl)
    .then(() => {
        shareButton.classList = 'btn btn-outline-success btn-small mx-1';
        document.getElementById('shareIcon').classList = 'bi-clipboard-check';
        setTimeout(() => {
            shareButton.classList = 'btn btn-primary btn-small mx-1';
            document.getElementById('shareIcon').classList = 'bi-share';
        }, 3000);
    });
});

document.getElementById('lockInButton').addEventListener('click', function(event) {
    event.target.setAttribute('disabled', '');
    lockedIn = true;
    connection.invoke('LockIn', gameCode);
});

document.getElementById('helpButton').addEventListener('click', function(event) {
    if (helpLegendControl !== null) {
        if (!helpLegendControl.getOptions().visible) {
            helpLegendControl.setOptions({
                visible: true
            });
        } else {
            helpLegendControl.setOptions({
                visible: false
            });
        }
    }
});

const urlSearchParams = new URLSearchParams(window.location.search);
if (urlSearchParams.has('gameCode')) {
    document.getElementById('gameCodeInput').value = urlSearchParams.get('gameCode');
}

function initMap() {
    map = new atlas.Map('map', {
        center: [-122.333301, 47.606501],
        zoom: 11,
        language: 'en-US',
        style: darkMode ? 'night' : 'road',
        authOptions: {
            authType: 'anonymous',
            clientId: 'df857d2c-3805-4793-90e4-63e84a499756',
            getToken: function(resolve, reject, map) {
                fetch('/api/AzureMaps')
                .then(r => r.text())
                .then(token => resolve(token));
            }
        }
    });

    map.events.add('ready', function() {
        if (isAdmin) {
            document.getElementById('startGameButton').removeAttribute('hidden');
            document.getElementById('shareButton').removeAttribute('hidden');
        }

        connection.invoke('GetRoundInfo', gameCode)
        .then((roundInfo) => {
            if (roundInfo.round !== 0) {
                if (roundInfo.roundEndTime) {
                    connection.invoke('GetRoundImage', gameCode)
                    .then(setRoundImage)
                    setRoundInfo(roundInfo);
                } else {
                    document.getElementById('roundText').innerText = `Round ${roundInfo.round} of ${roundInfo.numberOfRounds}`;
                }
            }
        });

        connection.invoke('GetPlayers', gameCode)
        .then((players) => {
            knownPlayers = players;
            playersLegendControl = new atlas.control.LegendControl({
                style: 'auto',
                showToggle: true,
                visible: true,
                legends: [{
                    type: 'html',
                    html: buildPlayerList(knownPlayers)
                }]
            });
            map.controls.add(playersLegendControl, { position: 'top-left '});
        });

        map.controls.add(new atlas.control.StyleControl({
            mapStyles: ['road', 'night', 'satellite_road_labels', 'high_contrast_dark', 'high_contrast_light']
        }), {
            position: 'bottom-left'
        });

        imageLegendControl = new atlas.control.LegendControl({
            style: 'auto',
            showToggle: true,
            visible: false,
            legends: [{
                type: 'html',
                html: '<div></div>'
            }]
        });
        map.controls.add(imageLegendControl, { position: 'bottom-right' });

        helpLegendControl = new atlas.control.LegendControl({
            style: 'auto',
            showToggle: false,
            visible: false,
            legends: [{
                type: 'html',
                html: getHelpText()
            }]
        });
        map.controls.add(helpLegendControl, { position: 'top-right' });
        
        map.events.add('click', function(e) {
            if (e && roundStarted && !lockedIn) {
                if (!atlas.data.BoundingBox.containsPosition(seattleBoundingBox, e.position)) {
                    popup.setOptions({
                        position: e.position,
                        content: `<div style="padding: 10px;">Guess not in Seattle</div>`
                    });
                    popup.open(map);
                    if (popupTimeout === null) {
                        popupTimeout = setTimeout(() => {
                            popup.close();
                            clearTimeout(popupTimeout);
                            popupTimeout = null;
                        }, 1500);
                    }
                    return;
                }
                connection.invoke('Guess', gameCode, e.position[1], e.position[0]);
                if (guessLocationDataSource == null) {
                    guessLocationDataSource = new atlas.source.DataSource(null, { cluster: false });
                    guessLocationDataSource.add(createSinglePointFeatureCollection(e.position));
                    map.sources.add(guessLocationDataSource);
                    const guessLocationSymbolLayer = new atlas.layer.SymbolLayer(guessLocationDataSource, null, {
                        iconOptions: {
                            image: 'marker-red'
                        },
                        textOptions: {
                            textField: ['get', 'content']
                        }
                    });
                    map.layers.add(guessLocationSymbolLayer);
                } else {
                    guessLocationDataSource.clear();
                    guessLocationDataSource.add(createSinglePointFeatureCollection(e.position));
                }
            }
        })
    });
}

function createInfoPopup(element, content) {
    const infoPopup = new bootstrap.Popover(element, {
        placement: 'top',
        content: content,
        trigger: 'manual'
    });
    infoPopup.show();
    const popoverTimeout = setTimeout(() => {
        infoPopup.hide();
        infoPopup.dispose();
        clearTimeout(popoverTimeout);
    }, 3000);
}

function updatePlayersLegend() {
    playersLegendControl.setOptions({
        legends: [{
            type: 'html',
            html: buildPlayerList(knownPlayers)
        }]
    });
}

function buildPlayerList(players, addHeader) {
    let list = '';
    if (addHeader === undefined || addHeader) {
        list = `<h6>Players</h6>`;
    }
    list += '<ol>';
    players.forEach(player => {
        let playerText = `<li>${player.username}: ${player.score}`;
        if (player.lastRoundScore) {
            playerText += `<span style="color: green;"> (+${player.lastRoundScore})</span>`;
        }
        playerText += '</li>';
        list += playerText;
    });
    list += '</ol>';
    return list;
}

function setRoundInfo(roundInfo) {
    roundStarted = true;
    clearEndRoundMapItems();
    if (preRoundModal !== null) {
        preRoundModal.hide();
    }
    document.getElementById('roundText').innerText = `Round ${roundInfo.round} of ${roundInfo.numberOfRounds}`;
    document.getElementById('roundTimeText').innerText = `Time left: ${roundInfo.roundLength}`;
    document.getElementById('lockInButton').removeAttribute('hidden');
    document.getElementById('lockInButton').removeAttribute('disabled');
    lockedIn = false;
    roundTimeRemaining = roundInfo.roundLength;
    roundTimerInterval = setInterval(() => {
        roundTimeRemaining -= 1;
        const roundTimeTextElement = document.getElementById('roundTimeText');
        if (roundTimeRemaining <= 0) {
            if (roundTimeTextElement.innerText !== 'Round over') {
                roundTimeTextElement.innerText = '';
            }
            clearInterval(roundTimerInterval);
        } else {
            roundTimeTextElement.innerText = `Time left: ${roundTimeRemaining}`;
        }
    }, 1000);
    map.setCamera({
        center: [-122.333301, 47.606501],
        zoom: 11,
        type: 'ease'
    });
}

function setRoundImage(imageInfo) {
    let type = '';
    if (imageInfo.type === 'gps') {
        type = 'Guess the location';
    } else if (imageInfo.type === 'intersection') {
        type = 'Guess the closest intersection';
    }

    let imageWidth = 400;
    if (window.innerWidth < 600) {
        imageWidth = 200;
    }

    imageLegendControl.setOptions({
        visible: true,
        minimized: false,
        legends: [{
            type: 'html',
            html: `<div style="display: flex; justify-content: space-between;"><span>${type}</span><button id="fullscreenImageButton" class="btn btn-light btn-sm"><i class="bi-arrows-fullscreen"></i></button></div><a href="${imageInfo.imageUrl}" target="_blank"><img id="guessImage" src="${imageInfo.imageUrl}" width="${imageWidth}"></img></a>`
        }]
    });

    document.getElementById('fullscreenImageButton').addEventListener('click', () => {
        if (!imageFullscreen) {
            const guessImage = document.getElementById('guessImage');
            const mapElement = document.getElementById('map');
            const heightMargin = 127;
            const widthMargin = 50;
            const widthRatio = (mapElement.offsetWidth - widthMargin) / guessImage.naturalWidth;
            const heightRatio = (mapElement.offsetHeight - heightMargin) / guessImage.naturalHeight;
            const ratio = Math.min(widthRatio, heightRatio);
            guessImage.removeAttribute('height');
            guessImage.setAttribute('width', guessImage.naturalWidth * ratio);
            imageFullscreen = true;
        } else {
            guessImage.removeAttribute('height');
            guessImage.setAttribute('width', getOriginalImageWidth());
            imageFullscreen = false;
        }
    });
}

function createUserId() {
    userId = '';
    const array = new Uint32Array(9);
    window.crypto.getRandomValues(array);
    for (const value of array) {
        const number = value % 10;
        userId += `${number}`;
    }
}

function getOriginalImageWidth() {
    if (window.innerWidth < 600) {
        return 200;
    }
    return 400;
}

function createSinglePointFeatureCollection(position, properties) {
    const featureCollection = new atlas.data.FeatureCollection([
        new atlas.data.Feature(new atlas.data.Point(position), properties)
    ]);
    return featureCollection;
}

function clearEndRoundMapItems() {
    if (guessLocationDataSource !== null) {
        guessLocationDataSource.clear();
    }
    if (distancesPointDataSource !== null) {
        distancesPointDataSource.clear();
    }
    if (distancesLineDataSource !== null) {
        distancesLineDataSource.clear();
    }
    if (realLocationDataSource !== null) {
        realLocationDataSource.clear();
    }
}

function createDisconnectionModal() {
    if (disconnectionModal === null) {
        disconnectionModal = new bootstrap.Modal('#disconnectionModal');
    }
}

function showError(message) {
    const alertDiv = document.getElementById('alertDiv');
    alertDiv.innerText = message;
    alertDiv.removeAttribute('hidden');
}
