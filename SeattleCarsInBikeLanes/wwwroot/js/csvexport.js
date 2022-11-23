import * as csv from './sync.js';

document.getElementById('downloadButton').addEventListener('click', () => {
    if (!reportedItemsPromise) {
        console.error('Data not loaded yet.');
    }

    reportedItemsPromise
    .then(reportedItems => {
        const formattedItems = reportedItems.map(item => {
            let latitude = null;
            let longitude = null;
            if (item.location && item.location.position) {
                latitude = item.location.position.latitude;
                longitude = item.location.position.longitude;
            }
    
            let imageString = null;
            if (item.imageUrls && item.imageUrls.length > 0) {
                imageString = item.imageUrls.join(';');
            } else if (item.imgurUrls && item.imgurUrls.length > 0) {
                imageString = item.imgurUrls.join(';');
            }

            const csvItem = {
                item_id: item.tweetId,
                tweet_posted_at: item.createdAt,
                number_of_cars: item.numberOfCars,
                date: item.date,
                time: item.time,
                location_string: item.locationString.replace('&amp;', '&'),
                latitude: latitude,
                longitude: longitude,
                image_urls: imageString
            };

            if (item.twitterLink) {
                csvItem.twitterLink = item.twitterLink;
            }

            if (item.mastodonLink) {
                csvItem.mastodonLink = item.mastodonLink;
            }
            
            return csvItem;
        });
    
        const newline = navigator.platform === 'Win32' ? 'windows' : 'unix';
    
        const csvstr = csv.stringify(formattedItems, {
            header: true,
            record_delimiter: newline
        });
        const blob = new Blob([csvstr], { type: 'text/csv' });
        const blobUrl = URL.createObjectURL(blob);
        const downloadTag = document.createElement('a');
        downloadTag.setAttribute('href', blobUrl);
        downloadTag.setAttribute('download', `seattlecarinbikelane_${new Date().getTime()}.csv`);
        downloadTag.click();
    });
});
