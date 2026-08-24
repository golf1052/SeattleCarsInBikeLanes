using Microsoft.AspNetCore.Mvc;
using SeattleCarsInBikeLanes.Controllers;

namespace SeattleCarsInBikeLanes.Tests
{
    public class PrivacyControllerTests
    {
        [Fact]
        public void Get_ReturnsPrivacyPage()
        {
            PrivacyController controller = new PrivacyController();

            VirtualFileResult result = Assert.IsType<VirtualFileResult>(controller.Get());

            Assert.Equal("privacy.html", result.FileName);
            Assert.Equal("text/html", result.ContentType);
        }

    }
}
