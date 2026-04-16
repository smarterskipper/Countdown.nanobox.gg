using HomelabCountdown.Models;

namespace HomelabCountdown.Services;

public class UtahPlaceService
{
    private static readonly PlaceInfo[] Places =
    [
        // ── National Parks ────────────────────────────────────────────────────
        new() { Name = "Zion National Park", Description = "Towering red and white sandstone cliffs, narrow slot canyons, and the Virgin River", Latitude = 37.2982, Longitude = -113.0263 },
        new() { Name = "Bryce Canyon", Description = "Thousands of crimson hoodoo spires rising from a natural amphitheater", Latitude = 37.5930, Longitude = -112.1871 },
        new() { Name = "Arches National Park", Description = "Over 2,000 natural stone arches including the iconic Delicate Arch", Latitude = 38.7331, Longitude = -109.5925 },
        new() { Name = "Canyonlands National Park", Description = "Vast canyons carved by the Colorado and Green Rivers with towering mesas", Latitude = 38.3269, Longitude = -109.8783 },
        new() { Name = "Capitol Reef National Park", Description = "Colorful Waterpocket Fold with towering cliffs and hidden slot canyons", Latitude = 38.0877, Longitude = -111.1505 },

        // ── National Monuments & Recreation Areas ─────────────────────────────
        new() { Name = "Monument Valley", Description = "Iconic sandstone buttes rising from the desert floor on the Utah-Arizona border", Latitude = 36.9833, Longitude = -110.0983 },
        new() { Name = "Grand Staircase-Escalante", Description = "Remote slot canyons, petrified forests, and layered sandstone wilderness", Latitude = 37.4000, Longitude = -111.5000 },
        new() { Name = "Natural Bridges National Monument", Description = "Three massive natural bridges carved by meandering streams through white sandstone", Latitude = 37.6043, Longitude = -110.0060 },
        new() { Name = "Cedar Breaks National Monument", Description = "A massive natural amphitheater at 10,000 feet with vivid orange and red rock colors", Latitude = 37.6355, Longitude = -112.8455 },
        new() { Name = "Lake Powell", Description = "Deep blue reservoir winding through red sandstone canyon walls", Latitude = 37.0736, Longitude = -111.2419 },

        // ── State Parks ───────────────────────────────────────────────────────
        new() { Name = "Dead Horse Point", Description = "Dramatic overlook 2,000 feet above the Colorado River and Canyonlands basin", Latitude = 38.4887, Longitude = -109.7388 },
        new() { Name = "Goblin Valley", Description = "Thousands of mushroom-shaped sandstone formations in a Mars-like desert landscape", Latitude = 38.5675, Longitude = -110.7022 },
        new() { Name = "Snow Canyon", Description = "Red and white Navajo sandstone cliffs with ancient lava fields and desert dunes", Latitude = 37.1828, Longitude = -113.6458 },
        new() { Name = "Kodachrome Basin", Description = "Towering sand pipes and colorful sandstone chimneys rising from the desert floor", Latitude = 37.4992, Longitude = -111.9939 },
        new() { Name = "Coral Pink Sand Dunes", Description = "Rolling pink sand dunes backed by towering sandstone cliffs", Latitude = 37.0365, Longitude = -112.7312 },

        // ── Lakes & Water ─────────────────────────────────────────────────────
        new() { Name = "Bear Lake", Description = "Turquoise Caribbean of the Rockies straddling the Utah-Idaho border", Latitude = 41.9619, Longitude = -111.3169 },
        new() { Name = "Mirror Lake", Description = "Pristine alpine lake at 10,200 feet in the Uinta Mountains reflecting surrounding peaks", Latitude = 40.7067, Longitude = -110.8825 },
        new() { Name = "Strawberry Reservoir", Description = "Vast high-mountain reservoir surrounded by rolling sage-covered hills", Latitude = 40.1700, Longitude = -111.1500 },
        new() { Name = "Flaming Gorge", Description = "Deep green reservoir cut through flaming red canyon walls on the Wyoming border", Latitude = 40.9138, Longitude = -109.4222 },
        new() { Name = "Fish Lake", Description = "Utah's largest natural mountain lake surrounded by golden aspen forests", Latitude = 38.5600, Longitude = -111.7025 },

        // ── Mountains & Canyons ───────────────────────────────────────────────
        new() { Name = "Uinta Mountains", Description = "Utah's highest range running east-west with Kings Peak at 13,534 feet", Latitude = 40.7500, Longitude = -110.5000 },
        new() { Name = "Wasatch Mountains", Description = "Dramatic front range rising 7,000 feet above the Salt Lake Valley", Latitude = 40.6000, Longitude = -111.5500 },
        new() { Name = "La Sal Mountains", Description = "Snow-capped peaks rising above the red desert near Moab", Latitude = 38.4750, Longitude = -109.2750 },
        new() { Name = "Logan Canyon", Description = "Limestone canyon with autumn maples, the Bear River, and towering cliffs", Latitude = 41.7400, Longitude = -111.5100 },
        new() { Name = "Provo Canyon", Description = "Steep canyon with Bridal Veil Falls and the rushing Provo River", Latitude = 40.3400, Longitude = -111.5800 },

        // ── Ski & Alpine ──────────────────────────────────────────────────────
        new() { Name = "Big Cottonwood Canyon", Description = "Towering granite walls, alpine meadows, and world-class ski terrain", Latitude = 40.6200, Longitude = -111.7200 },
        new() { Name = "Little Cottonwood Canyon", Description = "Sheer granite cliffs and the greatest snow on earth", Latitude = 40.5800, Longitude = -111.7500 },
        new() { Name = "Alpine Loop", Description = "Scenic mountain highway through golden aspens beneath Mount Timpanogos", Latitude = 40.4414, Longitude = -111.6181 },
        new() { Name = "Snowbird", Description = "Steep alpine terrain with year-round snow and wildflower meadows at 11,000 feet", Latitude = 40.5830, Longitude = -111.6508 },
        new() { Name = "Brian Head", Description = "Southern Utah's highest ski area at 11,307 feet above red rock country", Latitude = 37.6936, Longitude = -112.8497 },

        // ── Unique Features ───────────────────────────────────────────────────
        new() { Name = "Bonneville Salt Flats", Description = "Endless white salt flats stretching to the horizon under massive skies", Latitude = 40.7655, Longitude = -113.8868 },
        new() { Name = "Antelope Island", Description = "Largest island in the Great Salt Lake with free-roaming bison herds", Latitude = 41.0519, Longitude = -112.2457 },
        new() { Name = "Great Salt Lake", Description = "The largest saltwater lake in the Western Hemisphere with pink-tinged shallows", Latitude = 41.1700, Longitude = -112.5863 },
        new() { Name = "Timpanogos Cave", Description = "Ancient limestone caverns deep inside Mount Timpanogos with crystal formations", Latitude = 40.4439, Longitude = -111.7097 },
        new() { Name = "San Rafael Swell", Description = "Remote desert uplift with slot canyons, painted desert, and ancient rock art", Latitude = 38.9000, Longitude = -110.7000 },

        // ── Desert & Canyon ───────────────────────────────────────────────────
        new() { Name = "Escalante Petrified Forest", Description = "Ancient petrified wood scattered across a colorful desert plateau", Latitude = 37.7694, Longitude = -111.6075 },
        new() { Name = "Henry Mountains", Description = "The last mountain range charted in the continental US, home to wild bison", Latitude = 38.0000, Longitude = -110.7500 },
        new() { Name = "White Rim Trail", Description = "Dramatic mesa-top road circling the Island in the Sky in Canyonlands", Latitude = 38.4500, Longitude = -109.8200 },
        new() { Name = "Horseshoe Canyon", Description = "Remote canyon containing the Great Gallery of ancient Barrier Canyon rock art", Latitude = 38.4700, Longitude = -110.2000 },
        new() { Name = "Fantasy Canyon", Description = "Bizarre eroded sandstone formations in the Uinta Basin badlands", Latitude = 39.9500, Longitude = -109.3900 },

        // ── Towns & Valleys ───────────────────────────────────────────────────
        new() { Name = "Park City", Description = "Historic silver mining town turned ski resort in the Wasatch Back mountains", Latitude = 40.6461, Longitude = -111.4980 },
        new() { Name = "Moab", Description = "Red rock adventure town at the doorstep of Arches and Canyonlands", Latitude = 38.5733, Longitude = -109.5498 },
        new() { Name = "Kanab", Description = "Little Hollywood on the edge of Grand Staircase-Escalante and Vermilion Cliffs", Latitude = 37.0475, Longitude = -112.5263 },
        new() { Name = "Springdale", Description = "Gateway village beneath the towering sandstone walls of Zion Canyon", Latitude = 37.1897, Longitude = -112.9983 },
        new() { Name = "Heber Valley", Description = "Pastoral Swiss-style valley beneath the Wasatch Range with the Heber Creeper railroad", Latitude = 40.5070, Longitude = -111.4130 },

        // ── More Scenic ───────────────────────────────────────────────────────
        new() { Name = "Maple Canyon", Description = "Limestone sport climbing canyon with dense maple forests and cobblestone walls", Latitude = 39.6200, Longitude = -111.5400 },
        new() { Name = "Ogden Valley", Description = "Green mountain valley with Pineview Reservoir and surrounding Wasatch peaks", Latitude = 41.2500, Longitude = -111.7500 },
        new() { Name = "Jordanelle Reservoir", Description = "Mountain reservoir with panoramic views of Mount Timpanogos and Deer Creek", Latitude = 40.6000, Longitude = -111.4200 },
        new() { Name = "Deer Creek Reservoir", Description = "Narrow reservoir in Provo Canyon beneath steep mountain walls", Latitude = 40.4200, Longitude = -111.5100 },
        new() { Name = "Skull Valley", Description = "Wide desert valley west of the Stansbury Mountains with wild mustang herds", Latitude = 40.6300, Longitude = -112.7800 },
    ];

    public PlaceInfo GetPlaceForDate(DateOnly date)
    {
        // Deterministic pick seeded by date so the same date always gets the same place
        var rng = new Random(date.DayNumber * 31 + 17);
        var idx = rng.Next(Places.Length);
        var template = Places[idx];

        return new PlaceInfo
        {
            Name = template.Name,
            Description = template.Description,
            Latitude = template.Latitude,
            Longitude = template.Longitude,
            Date = date
        };
    }
}
